using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static TenantRunContinuationService CredentialContinuations(Fixture f, ITenantCredentialOAuthVerifier verifier)
    {
        var store = f.Iam; var iam = new IamAuthorizer(store);
        var operations = new TenantOperationAuthorizer(store, (ITenantRunStore)store, (ITenantAgentStore)store, iam,
            new((ITenantAuthorizationAuditStore)store, f.Ids, f.Clock));
        var local = new TenantApprovedOperationService(store, (ITenantRunStore)store, (ITenantAgentStore)store,
            (ITenantApprovalStore)store, (ITenantApprovedOperationStore)store, operations, f.RunConfiguration(), f.Ids, f.Clock,
            new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(store), SyntheticProviderAliases.Default);
        return new((ITenantRunStore)store, (ITenantApprovalStore)store, local, f.Clock, (ITenantRuntimeStore)store, f.Ids, RuntimeChecks(f, verifier));
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(true, false, false)]
    [InlineData(false, true, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(true, false, true)]
    public async Task Credential_successor_consumes_only_the_exact_checkpoint_and_charges_provider_time_without_human_wait(bool sqlite, bool mismatch, bool exhausted)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T13:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await RuntimeCredential(f, false);
        var storage = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var call = new ModelToolCall("check", ToolCatalog.RequestAuthorization, """{"operation":"check_credential_connection","reference":"work-drive"}""");
        var run = await storage.ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Checkpoint(setup.Run.Revision, clock.GetUtcNow(), setup.Run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        var approvals = RuntimeApprovals(f);
        var request = (await approvals.PrepareCredentialCheckAsync(new(setup.Tenant, run.AgentRunId), "work-drive", run.Revision, run.Claim!.Generation)).Request!;
        clock.Advance(TimeSpan.FromMinutes(20));
        var view = await approvals.ReadViewAsync(setup.Tenant, request.RequestId, setup.Owner);
        await approvals.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await approvals.DispatchPendingAsync();
        var source = (await runs.FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        Assert.Equal(AgentRunOutcomeKind.Continued, source.Result!.OutcomeKind);
        var queued = (await runs.FindTenantRunAsync(setup.Tenant, source.Result.ContinuationRunId!.Value))!;
        var claimed = await storage.ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        if (mismatch)
        {
            var wrong = call with { ArgumentsJson = call.ArgumentsJson.Replace("work-drive", "other-drive") };
            claimed = await storage.ApplyTenantRuntimeCommandAsync(claimed.Owner, claimed.AgentRunId,
                new AgentRunCommand.Checkpoint(claimed.Revision, clock.GetUtcNow(), claimed.Claim!.Generation,
                    new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [wrong])]), 1, 0, 10000, 100), null), f.Ids.NewId());
        }
        var verifier = new RuntimeCheckVerifier((_, _) => { clock.Advance(TimeSpan.FromSeconds(exhausted ? 11 : 2)); return ValueTask.FromResult(new CredentialOAuthCheck()); });
        var service = CredentialContinuations(f, verifier);
        if (mismatch)
        {
            await Assert.ThrowsAsync<SecurityConflictException>(() => service.PrepareExternalEffectAsync(claimed).AsTask());
            Assert.Equal(0, verifier.Calls); Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
            Assert.Equal(OperationGrantStatus.Available, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
            return;
        }
        await service.PrepareExternalEffectAsync(claimed);
        var resumed = await service.ResumeAsync(claimed);
        Assert.Equal(exhausted ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, resumed.Outcome!.Outcome);
        Assert.Equal(exhausted ? 0 : 8000, resumed.Run.Checkpoint!.RemainingOverallBudgetMs); Assert.Equal(exhausted ? 10100 : 2100, resumed.Run.Checkpoint.ActiveExecutionMs);
        Assert.True(AgentRunToolCallCheckpoint.TryRead(resumed.Run.Checkpoint, out var messages));
        var result = Assert.Single(messages!, m => m.Role == ModelRole.Tool && m.ToolCallId == call.Id);
        using var payload = JsonDocument.Parse(result.Text); Assert.Equal(exhausted ? JsonValueKind.Null : JsonValueKind.True, payload.RootElement.GetProperty("applied").ValueKind);
        if (!exhausted) await service.PrepareExternalEffectAsync(resumed.Run);
        Assert.Equal(resumed.Run.Revision, (await service.ResumeAsync(resumed.Run)).Run.Revision); Assert.Equal(1, verifier.Calls);
        Assert.Equal(resumed.Run.AgentRunId, (await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId))!.RunId);
        Assert.Equal(exhausted ? OperationGrantStatus.Indeterminate : OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId))!.Status);
    }
}
