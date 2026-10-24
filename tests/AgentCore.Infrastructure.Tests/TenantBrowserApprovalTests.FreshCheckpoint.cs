using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_fresh_observation_or_refusal_and_checkpoint_never_redeem_a_sealed_operation_for_replay(bool sqlite, bool uncertain)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f);
        var target = Probe(s); var native = new EffectProbe(target) { Uncertain = uncertain };
        (s, var request) = await ApprovedEffect(f, s, native); var effects = Effects(f, native);
        var first = await ExecuteEffect(effects, s, request); Assert.Equal(uncertain ? TenantOperationOutcome.Indeterminate : TenantOperationOutcome.Completed, first.Receipt.Outcome);
        Assert.Equal(1, native.NativeEffects);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!; s = s with { Run = run };
        var observation = new ReadProbe(target); string result;
        if (uncertain)
        {
            var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => ReadService(f, observation).InspectAsync(Frame(s), ReadCall("fresh-after-sealed"), 32768).AsTask());
            Assert.Equal("browser_effect_in_progress", denied.ReasonCode); Assert.Equal(0, observation.Reads);
            Assert.Throws<AgentRunTransitionException>(() => run.AcceptBrowserSnapshot(run.Revision, run.Claim!.Generation, f.Clock.GetUtcNow()));
            result = JsonSerializer.Serialize(new { error = denied.ReasonCode });
        }
        else
        {
            result = await ReadService(f, observation).InspectAsync(Frame(s), ReadCall("fresh-after-sealed"), 32768);
            using var fresh = JsonDocument.Parse(result); Assert.True(fresh.RootElement.GetProperty("readAuthorized").GetBoolean());
            Assert.True(fresh.RootElement.GetProperty("tenantBrowserRead").GetBoolean()); Assert.Equal(1, observation.Reads); Assert.Equal(2, target.CommandRevision);
        }
        var payload = AgentRunToolCallCheckpoint.Write([
            new(ModelRole.Assistant, "", ToolCalls: [ReadCall("fresh-after-sealed")]),
            new(ModelRole.Tool, result, ToolCallId: "fresh-after-sealed", Name: ToolCatalog.BrowserSnapshot)
        ]);
        var prior = run.Checkpoint;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation,
                new(payload, (prior?.StepCount ?? 0) + 1, Math.Max(prior?.OutputBytes ?? 0, System.Text.Encoding.UTF8.GetByteCount(result)),
                    prior?.RemainingOverallBudgetMs ?? 10000, prior?.ActiveExecutionMs), null), f.Ids.NewId());
        Assert.Equal(JsonSerializer.Serialize(s.Run.SideEffect), JsonSerializer.Serialize(run.SideEffect)); Assert.Equal(payload, run.Checkpoint!.PayloadJson);
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: f.Clock) : f.Iam;
        var retained = (await ((ITenantRunStore)reopened).FindTenantRunAsync(s.Tenant, run.AgentRunId))!;
        Assert.Equal(payload, retained.Checkpoint!.PayloadJson); Assert.Equal(JsonSerializer.Serialize(run.SideEffect), JsonSerializer.Serialize(retained.SideEffect));
        var repeated = await ExecuteEffect(Effects(f, native, reopened), s with { Run = retained }, request);
        Assert.Equal(first.Receipt, repeated.Receipt); Assert.Null(repeated.Result); Assert.Equal(1, native.NativeEffects); Assert.Equal(1, native.Invocations);
        Assert.Equal(first.Receipt, await ((ITenantApprovedOperationStore)reopened).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId));
    }
}
