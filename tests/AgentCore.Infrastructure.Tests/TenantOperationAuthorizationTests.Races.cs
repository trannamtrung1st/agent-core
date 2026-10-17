using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "revoke")] [InlineData(true, "revoke")]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Policy_commit_fences_original_human_session_current_authority_and_competing_policy(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var governor = await User(f, setup, "governor@example.test", ["tenant.governance.manage"]);
        var assignment = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Assignments.Single(a => a.PrincipalId == governor.PrincipalId);
        var gate = new PolicyGate((ITenantOperationPolicyStore)f.Iam); using var cancelled = new CancellationTokenSource();
        var pending = Policies(f, gate).SaveAsync(setup.Tenant, governor, "agents.read", new(OperationApprovalMode.Always, false), await Revision(), 0, cancelled.Token).AsTask();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (boundary == "logout") await f.Identity.SignOutAsync(governor.AuthenticationSessionId);
            if (boundary == "revoke") await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
            if (boundary == "compete") await Policies(f).SaveAsync(setup.Tenant, setup.Owner, "agents.read", new(OperationApprovalMode.Always, true), await Revision(), 0);
            if (boundary == "cancel") cancelled.Cancel();
        }
        finally { gate.Proceed.TrySetResult(); }
        if (boundary == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else if (boundary == "logout") await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        else await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        var policies = (await f.Iam.ReadTenantAsync(setup.Tenant))!.OperationPolicies;
        if (boundary == "compete") Assert.True(Assert.Single(policies).IndependentReview); else Assert.Empty(policies);
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "tenant.operation_policy.update" && a.ActorPrincipalId == governor.PrincipalId);
    }
    private sealed class PolicyGate(ITenantOperationPolicyStore inner) : ITenantOperationPolicyStore
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<bool> TrySaveTenantOperationPolicyAsync(TenantSecurityState next, long expected, TenantSessionInitiator initiator, AuthorizationAudit audit, CancellationToken ct = default)
        { Entered.TrySetResult(); await Proceed.Task.WaitAsync(ct); return await inner.TrySaveTenantOperationPolicyAsync(next, expected, initiator, audit, ct); }
    }
}
