using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_authorization_audit_acknowledgement_prevents_a_real_preference_effect_and_recovers_without_partial_state(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var run = setup.Run;
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => Service(f, audits: new UnavailableAudit()).ExecuteAsync(
            new(setup.Tenant, run.AgentRunId), run.Claim!.Generation, "audited-call", Preferences).AsTask());
        Assert.Equal("stale_revision", error.ReasonCode);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Revision);
        Assert.Equal(setup.Agent.Instance.Revision, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Tenant, run.AgentInstanceId))!.Instance.Revision);
        Assert.Null(await ((ITenantPreferenceOperationStore)f.Iam).FindDirectOperationReceiptAsync(setup.Tenant, run.AgentRunId, "audited-call"));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.preferences.apply");
        var recovered = await Service(f).ExecuteAsync(new(setup.Tenant, run.AgentRunId), run.Claim!.Generation, "audited-call", Preferences);
        Assert.Equal(OperationDecisionKind.Allowed, recovered.Decision); Assert.NotNull(recovered.Receipt);
        var result = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "operations.preferences.apply");
        var e = Assert.IsType<ExecutionAuthorizationAudit>(result.Execution);
        Assert.Equal(ExecutionAuthorizationPurpose.EffectResult, e.Purpose); Assert.Equal(run.Admission.ExecutionContext, e.Context);
        Assert.Equal(recovered.Receipt.OperationId, e.OperationId); Assert.Equal(run.Claim.Generation, e.ClaimGeneration);
        Assert.Equal(revision, e.SecurityRevision); Assert.Null(e.ApprovalRequestId); Assert.Null(e.GrantId);
    }

    private sealed class UnavailableAudit : ITenantAuthorizationAuditStore
    {
        public ValueTask<bool> TryRecordOperationAuthorizationAsync(AuthorizationAudit audit, CancellationToken ct = default) => ValueTask.FromResult(false);
    }
}
