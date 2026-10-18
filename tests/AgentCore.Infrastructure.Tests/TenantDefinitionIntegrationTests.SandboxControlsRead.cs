using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static TenantSandboxRuntimeReadService SandboxControlReads(Fixture f, RuntimeSandboxSetup s, ISandboxImageAuthority? images = null)
    {
        var p = SetupProfiles(f);
        return new(f.Iam, (ITenantSessionStore)f.Iam, (ITenantSandboxRuntimeStore)f.Iam, f.Identity, f.Authorizer,
            runConfiguration: s.Resolver, profileStore: (ITenantSandboxProfileStore)f.Iam, images: images ?? p.Images, readiness: p.Ready, host: p.Host);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_controls_read_supplies_exact_commands_without_dispatch_or_permission_expansion(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true);
        var store = (ITenantSandboxRuntimeStore)f.Iam; var reads = SandboxControlReads(f, s);
        var empty = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.Null(empty.Observation.Environment); Assert.False(empty.CanReset); Assert.False(empty.CanStop);
        Assert.All(empty.Profiles, p => Assert.False(p.CanSelect)); Assert.Equal(2, empty.Profiles.Count);
        Assert.Equal(s.Lease.Profile.Id, empty.DefaultProfileId);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready, ProviderId = "PRIVATE_PROVIDER" }, 1));
        var view = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.True(view.CanReset); Assert.False(view.CanStop); Assert.False(view.Observation.LiveAuthorityConfirmed);
        Assert.False(Assert.Single(view.Profiles, p => p.Id == s.Lease.Profile.Id).CanSelect);
        var target = Assert.Single(view.Profiles, p => p.Id == s.Alternate!.Id); Assert.True(target.CanSelect);
        Assert.DoesNotContain("PRIVATE_PROVIDER", JsonSerializer.Serialize(view));
        Assert.Null(await f.Sandbox.GetLeaseAsync(s.Session.SessionId));
        var p = SetupProfiles(f); var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store,
            f.Identity, f.Authorizer, f.Ids, TimeProvider.System, SetupResolver(f), p.Images, p.Ready, p.Host);
        var lease = view.Observation.Environment!;
        var receipt = await controls.RequestProfileSwitchAsync(s.Tenant, s.Human,
            new(s.Session.SessionId, view.Observation.SessionRevision, lease.Generation, lease.Revision, lease.ActiveExecutionId ?? lease.LastExecutionId,
                target.Id, target.Revision, view.InstanceRevision), view.Observation.SecurityRevision);
        Assert.False(receipt.RetirementConfirmed); Assert.Equal(0, p.Runner.Calls);
        var pending = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.True(pending.Observation.Environment!.CleanupPending); Assert.All(pending.Profiles, x => Assert.False(x.CanSelect));
        Assert.Equal(target.Id, pending.Observation.Environment.Selection!.ProfileId);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_controls_read_filters_choices_and_requires_independent_session_and_agent_management(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, alternate: true); var reads = SandboxControlReads(f, s);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var minimal = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Minimal", ["agents.chat"], await Revision());
        var account = await f.Identity.RegisterAsync("controls-reader@example.test", "Reader", "strong-reader-password");
        var invite = await f.Access.InviteAsync(s.Human.PrincipalId, s.Tenant, account.LoginKey, minimal.RoleId, await Revision());
        var member = await f.Access.AcceptInvitationAsync(account.AccountId, s.Tenant, invite.Invitation.InvitationId, invite.Token);
        var human = await SetupHuman(f, account.AccountId, member.PrincipalId, account.LoginKey, "strong-reader-password");
        async Task<Guid> Grant(string permission, AccessScope scope) {
            var role = await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, permission + scope.ResourceType, [permission], await Revision());
            return (await f.Access.AssignRoleAsync(s.Human.PrincipalId, s.Tenant, human.PrincipalId, role.RoleId, scope, await Revision())).AssignmentId;
        }
        await Grant("sessions.read", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        var agentScope = AccessScope.ForResource(s.Tenant, "agent", s.Session.AgentInstanceId.ToString("D"));
        await Grant("sandbox.read", agentScope);
        var profileGrant = await Grant("sandbox.read", TenantSandboxProfileRules.Scope(s.Tenant, s.Alternate!.Id));
        var partial = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human);
        Assert.Null(partial.DefaultProfileId); Assert.Equal(s.Alternate.Id, Assert.Single(partial.Profiles).Id); Assert.False(partial.CanReset);
        var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write with { SecurityRevision = await Revision() }, s.Lease, 0, DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human).AsTask());
        await Grant("sandbox.read", TenantSandboxProfileRules.Scope(s.Tenant, s.Lease.Profile.Id));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        Assert.False((await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human)).CanReset);
        await Grant("sessions.manage", TenantSessionRules.Scope(s.Tenant, s.Session.SessionId));
        Assert.False((await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human)).CanReset);
        await Grant("sandbox.manage", agentScope);
        var managed = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human);
        Assert.True(managed.CanReset); Assert.True(Assert.Single(managed.Profiles, x => x.Id == s.Alternate.Id).CanSelect);
        Assert.False((await f.Authorizer.CheckAsync(human.PrincipalId, "agents.update", agentScope)).Allowed);
        await f.Access.RevokeAssignmentAsync(s.Human.PrincipalId, s.Tenant, profileGrant, await Revision());
        Assert.DoesNotContain((await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, human)).Profiles, x => x.Id == s.Alternate.Id);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_controls_read_supplies_exact_Stop_and_retires_the_action_after_consent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var execution = RuntimeExecution(f, s); Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write, execution, DateTimeOffset.UtcNow));
        var reads = SandboxControlReads(f, s); var view = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.True(view.CanStop); Assert.True(view.CanReset); Assert.All(view.Profiles, x => Assert.False(x.CanSelect));
        var lease = view.Observation.Environment!; var e = view.Observation.Execution!;
        var controls = new TenantSandboxControlService(f.Iam, (ITenantSessionStore)f.Iam, store, f.Identity, f.Authorizer, f.Ids, TimeProvider.System);
        await controls.RequestProcessStopAsync(s.Tenant, s.Human,
            new(s.Session.SessionId, view.Observation.SessionRevision, lease.Generation, lease.Revision, e.ExecutionId, e.Revision), view.Observation.SecurityRevision);
        var pending = await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.False(pending.CanStop); Assert.True(pending.Observation.Execution!.StopRequested);
        Assert.True(pending.Observation.Environment!.CleanupPending); Assert.False(pending.Observation.Execution.TerminationConfirmed);
    }

    [Theory] [InlineData(false, "logout")] [InlineData(true, "logout")]
    [InlineData(false, "security")] [InlineData(true, "security")]
    [InlineData(false, "lease")] [InlineData(true, "lease")]
    public async Task Tenant_sandbox_controls_read_rejects_changes_during_source_inspection(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        var hook = new SetupImageHook(SetupProfiles(f).Images) { After = async () => {
            if (change == "logout") { await f.Identity.SignOutAsync(s.Human.AuthenticationSessionId); _ = await SetupHuman(f, s.Human.AccountId, s.Human.PrincipalId); }
            else if (change == "security") await f.Access.SaveRoleAsync(s.Human.PrincipalId, s.Tenant, null, "Concurrent change", ["agents.read"], (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
            else Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        } };
        var reads = SandboxControlReads(f, s, hook);
        if (change == "logout") await Assert.ThrowsAsync<AccessDeniedException>(() => reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human).AsTask());
        else await Assert.ThrowsAsync<SecurityConflictException>(() => reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human).AsTask());
        var currentHuman = change == "logout" ? await SetupHuman(f, s.Human.AccountId, s.Human.PrincipalId) : s.Human;
        Assert.NotNull((await reads.ReadControlsAsync(s.Tenant, s.Session.SessionId, currentHuman)).Observation.Environment);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_sandbox_controls_read_keeps_cleanup_available_when_current_source_cannot_accept_saved_setup(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, s.Lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, s.Lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var p = SetupProfiles(f); async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var configuration = ConfigurationServices(f, sandboxImages: p.Images, sandboxReadiness: p.Ready).Configuration;
        await configuration.PatchSettingsAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, "sandboxPolicy", await Revision(), 1,
            Fields(new { cpu = .75 }), [], initiator: s.Human);
        await p.Profiles.SaveAsync(s.Tenant, s.Lease.Profile.Id, s.Human, SetupInput("Reduced source", .5), await Revision(), 1);
        await p.Verify.VerifyAsync(s.Tenant, s.Lease.Profile.Id, s.Human, await Revision(), 2);
        var view = await SandboxControlReads(f, s).ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human);
        Assert.True(view.CanReset); Assert.Equal(2, view.InstanceRevision);
        var source = Assert.Single(view.Profiles); Assert.Equal("Eligible", source.Availability); Assert.False(source.CanSelect);
        await p.Profiles.SaveAsync(s.Tenant, s.Lease.Profile.Id, s.Human, SetupInput("Disabled source", .5) with { Enabled = false }, await Revision(), 2);
        source = Assert.Single((await SandboxControlReads(f, s).ReadControlsAsync(s.Tenant, s.Session.SessionId, s.Human)).Profiles);
        Assert.Equal("Disabled", source.Availability); Assert.False(source.CanSelect); Assert.Equal(1, p.Runner.Calls);
    }
}
