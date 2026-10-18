using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserApprovalTests
{
    private static async Task<(TenantSessionInitiator Human, RoleAssignment Exception)> PasswordReviewer(Fixture f, Setup s,
        TenantApprovalService approvals, TenantApprovalRequest request)
    {
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("password-reviewer@example.test", "Scoped reviewer", "strong-reviewer-password");
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, state.Roles.Single(r => r.Key == "approver").RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        foreach (var assignment in (await f.Iam.ReadTenantAsync(s.Tenant))!.Assignments.Where(a => a.PrincipalId == user.PrincipalId))
            await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, assignment.AssignmentId, await Revision());
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var human = new TenantSessionInitiator(account.AccountId, login.SessionId, user.PrincipalId);
        async Task<RoleAssignment> Grant(string name, string[] permissions, AccessScope scope)
        {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, name, permissions, await Revision());
            return await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, user.PrincipalId, role.RoleId, scope, await Revision());
        }
        await Grant("Read inbox", ["approvals.read"], AccessScope.ForTenant(s.Tenant));
        await Grant("Exact Browser", ["browser.read", "browser.use", "approvals.approve"], AccessScope.ForResource(s.Tenant, "agent", s.Agent.Instance.InstanceId.ToString("D")));
        await Grant("Exact Session", ["sessions.read", "approvals.approve"], TenantSessionRules.Scope(s.Tenant, s.Run.SessionId));
        var credential = TenantCredentialRules.Scope(s.Tenant, s.Password!.Credential.Value.CredentialId);
        await Grant("Exact Credential", ["credentials.read", "credentials.use", "approvals.approve"], credential);
        Assert.False((await approvals.ReadViewAsync(s.Tenant, request.RequestId, human)).Review!.CanDecide);
        await Assert.ThrowsAsync<AccessDeniedException>(() => approvals.DecideAsync(s.Tenant, request.RequestId, human, true,
            request.Operation.ActionHash, (f.Iam.ReadTenantAsync(s.Tenant).Result)!.Tenant.Revision, request.Revision).AsTask());
        var exception = await Grant("Exact exception", ["approvals.grant_exception"], credential);
        Assert.True((await approvals.ReadViewAsync(s.Tenant, request.RequestId, human)).Review!.CanDecide);
        return (human, exception);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_password_exception_rechecks_scoped_grantor_before_dispatch_and_at_native_gate(bool sqlite, bool atGate)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await Prepare(f, password: true, credentialUseAllowed: false); var probe = new EffectProbe(Probe(s)); var approvals = Service(f, probe);
        var request = (await Request(approvals, s, ToolCatalog.BrowserFillCredential, PasswordArguments)).Request!;
        var reviewer = await PasswordReviewer(f, s, approvals, request);
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, reviewer.Human);
        request = await approvals.DecideAsync(s.Tenant, request.RequestId, reviewer.Human, true, request.Operation.ActionHash, view.SecurityRevision, request.Revision);
        await approvals.DispatchPendingAsync(); var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        s = s with { Run = run };
        async Task Revoke() => await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, reviewer.Exception.AssignmentId, (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        if (atGate) probe.BeforeGate = Revoke; else await Revoke();
        var effects = Effects(f, probe); var result = await ExecuteEffect(effects, s, request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Receipt.Outcome); Assert.Equal("grantor_authority_lost", result.Receipt.ReasonCode);
        Assert.Equal(0, probe.NativeEffects); Assert.Equal(0, probe.PasswordResolutions); Assert.Equal(atGate ? 1 : 0, probe.Invocations);
        var grant = (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId))!;
        Assert.Equal(OperationGrantStatus.Revoked, grant.Status); Assert.Equal(atGate ? 3 : 2, grant.Revision);
        Assert.Equal(result.Receipt, (await ExecuteEffect(effects, s, request)).Receipt);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_exception_unknown_attempt_is_terminal_and_cannot_authorize_another_command(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await Prepare(f, password: true, credentialUseAllowed: false); var probe = new EffectProbe(Probe(s)) { Uncertain = true };
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserFillCredential, args: PasswordArguments);
        var effects = Effects(f, probe); var result = await ExecuteEffect(effects, s, request);
        Assert.Equal(TenantOperationOutcome.Indeterminate, result.Receipt.Outcome); Assert.Null(result.Result);
        var grant = (await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId))!;
        Assert.Equal(OperationGrantStatus.Indeterminate, grant.Status); Assert.Equal(3, grant.Revision);
        Assert.Equal(result.Receipt, (await ExecuteEffect(effects, s, request)).Receipt); Assert.Equal(1, probe.NativeEffects); Assert.Equal(1, probe.PasswordResolutions);
        await Assert.ThrowsAsync<SecurityConflictException>(() => effects.ExecuteAsync(new(s.Tenant, s.Run.AgentRunId), request.RequestId,
            f.Ids.NewId(), s.Run.Claim!.Generation, request.Operation.ActionHash).AsTask());
        Assert.Equal(1, probe.Invocations);
    }

    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Browser_password_abandoned_Review_or_exception_recovers_after_SQLite_reopen_without_secret_or_native_replay(bool sqlite, bool exceptional)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var s = await Prepare(f, password: true, credentialUseAllowed: !exceptional); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserFillCredential, args: PasswordArguments);
        var store = (ITenantApprovedOperationStore)f.Iam; var decision = (await store.FindApprovalDecisionAsync(s.Tenant, request.RequestId))!;
        var grant = await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId); var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var commit = new TenantOperationCommit(request.RequestId, request.Operation.OperationId, s.Run.AgentRunId, s.Run.Claim!.Generation,
            s.Run.Revision, request.Revision, decision.DecisionId, grant?.Revision, request.Operation.ActionHash, TenantOperationOutcome.Completed, "operation_completed");
        var start = (await store.TryStartBrowserEffectAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, commit,
            new(f.Ids.NewId(), s.Tenant, s.Agent.PrincipalId, TenantBrowserEffectRules.DispatchAction, request.ResourceId, "allowed", "browser_command_dispatched", clock.GetUtcNow())))!;
        Assert.NotNull(start.Fence); Assert.Equal(0, await Effects(f, probe).RecoverAsync()); clock.Advance(TimeSpan.FromMinutes(6));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns, clock: clock) : f.Iam;
        var effects = Effects(f, probe, reopened); Assert.Equal(1, await effects.RecoverAsync()); Assert.Equal(0, await effects.RecoverAsync());
        var receipt = (await ((ITenantApprovedOperationStore)reopened).FindOperationReceiptAsync(s.Tenant, request.Operation.OperationId))!;
        Assert.Equal(TenantOperationOutcome.Indeterminate, receipt.Outcome); Assert.Equal(TenantBrowserEffectRules.Uncertain, receipt.ReasonCode);
        Assert.Equal(0, probe.Invocations); Assert.Equal(0, probe.PasswordResolutions);
        var recoveredGrant = await ((ITenantApprovalStore)reopened).FindOperationGrantAsync(s.Tenant, request.RequestId);
        if (exceptional) { Assert.NotNull(recoveredGrant); Assert.Equal(OperationGrantStatus.Indeterminate, recoveredGrant.Status); Assert.Equal(3, recoveredGrant.Revision); }
        else Assert.Null(recoveredGrant);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_Review_cannot_become_exceptional_when_executor_use_is_removed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Prepare(f, password: true); var probe = new EffectProbe(Probe(s));
        (s, var request) = await ApprovedEffect(f, s, probe, tool: ToolCatalog.BrowserFillCredential, args: PasswordArguments);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!; var role = state.Roles.Single(r => r.RoleId == s.CredentialUse!.RoleId);
        await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, role.RoleId, role.Name, ["credentials.read"], state.Tenant.Revision);
        var result = await ExecuteEffect(Effects(f, probe), s, request);
        Assert.Equal(TenantOperationOutcome.Denied, result.Receipt.Outcome); Assert.Equal("permission_denied", result.Receipt.ReasonCode);
        Assert.Equal(0, probe.Invocations); Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(s.Tenant, request.RequestId));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Browser_password_exception_never_supplies_independent_Browser_use_permission(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var s = await Prepare(f, permissions: false, password: true, credentialUseAllowed: false); var probe = Probe(s);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Request(Service(f, probe), s, ToolCatalog.BrowserFillCredential, PasswordArguments).AsTask());
        Assert.Equal(0, probe.Reads); Assert.Empty((await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(s.Tenant, null, null, new())).Items);
    }
}
