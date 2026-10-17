using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    private static TenantRunContinuationService Continuations(Fixture f, IIamStore? store = null)
    {
        var current = store ?? f.Iam;
        return new((ITenantRunStore)current, (ITenantApprovalStore)current, Effects(f, current), f.Clock, (ITenantRuntimeStore)current, f.Ids);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Approved_continuation_reuses_receipt_after_lease_recovery_and_reopen_without_reapplying_or_accepting_the_old_generation(bool sqlite, bool review)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await EffectSetup(f, review);
        var first = await Continuations(f).ResumeAsync(setup.Run);
        Assert.Equal(TenantOperationOutcome.Completed, first.Outcome!.Outcome); Assert.Equal("operation_completed", first.Outcome.ReasonCode);
        Assert.Equal(setup.Run.Revision + 1, first.Run.Revision);
        var tenant = setup.Request.TenantId;
        var securityRevision = (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        clock.Advance(TimeSpan.FromMinutes(6));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: clock) : f.Iam;
        var storage = (ITenantRuntimeStore)reopened;
        var recovered = await storage.ApplyTenantRuntimeCommandAsync(first.Run.Owner, first.Run.AgentRunId,
            new AgentRunCommand.Recover(first.Run.Revision, clock.GetUtcNow()), f.Ids.NewId());
        var claimed = await storage.ApplyTenantRuntimeCommandAsync(recovered.Owner, recovered.AgentRunId,
            new AgentRunCommand.Claim(recovered.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var service = Continuations(f, reopened);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ResumeAsync(first.Run).AsTask());
        var replay = await service.ResumeAsync(claimed);
        Assert.Equal(first.Outcome, replay.Outcome); Assert.Equal(claimed.Revision, replay.Run.Revision);
        Assert.Equal(securityRevision, (await reopened.ReadTenantAsync(tenant))!.Tenant.Revision);
        Assert.Equal(3, (await ((ITenantAgentStore)reopened).FindTenantAgentAsync(tenant, setup.Request.Operation.InstanceId))!.Instance.Revision);
        Assert.Single(await reopened.ReadAuditAsync(tenant, 200), a => a.Action == "operations.persona.apply");
        if (!review) Assert.Equal(OperationGrantStatus.Consumed, (await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(tenant, setup.Request.RequestId))!.Status);
    }

    [Theory]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "expire")] [InlineData(true, "expire")]
    public async Task Cancelled_or_expired_continuation_records_no_effect_and_supplies_the_stable_denial(bool sqlite, string disposition)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        var approval = ApprovalService(f);
        var request = (await approval.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Never applied" }, 1)).Request!;
        if (disposition == "cancel") await approval.CancelAsync(setup.Tenant, request.RequestId, setup.Owner, request.Operation.ActionHash,
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        else clock.Advance(TimeSpan.FromHours(1));
        await approval.DispatchPendingAsync();
        var runs = (ITenantRunStore)f.Iam; var queued = (await runs.FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!;
        var claimed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(queued.Owner, queued.AgentRunId,
            new AgentRunCommand.Claim(queued.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var result = await Continuations(f).ResumeAsync(claimed);
        Assert.Equal(TenantOperationOutcome.Denied, result.Outcome!.Outcome);
        Assert.Equal(disposition == "cancel" ? "approval_cancelled" : "approval_expired", result.Outcome.ReasonCode);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Tenant, request.Operation.OperationId));
        Assert.Equal(setup.Agent.Instance.Persona, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, setup.Agent.Instance.InstanceId))!.Instance.Persona);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.persona.apply");
    }
}
