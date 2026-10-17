using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Run_current_authority_observes_adopted_capabilities_and_isolation_without_rewriting_admission(bool sqlite, bool removeProfile)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f);
        var authority = new TenantRunExecutionAuthority(new(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
            new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, TimeProvider.System)), f.Iam, TimeProvider.System, s.Resolver);
        Assert.True(RolePermissions.AllowsTool((await authority.CurrentDefinitionAsync(s.Run))!, ToolCatalog.SandboxExec));
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var draft = await f.Service.ForkAsync(s.Human.PrincipalId, s.Tenant, Guid.Parse(s.Run.DefinitionId), 1, await Revision());
        draft = await f.Service.UpdateAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId, draft.Candidate with {
            AgentInstructions = "New instructions must not rewrite this Run.",
            Environment = new(Capabilities: new("Selected", removeProfile ? [ToolCatalog.SandboxExec] : [ToolCatalog.WorkspaceRead])),
            SandboxPolicy = removeProfile ? null : draft.Candidate.SandboxPolicy,
            StateIsolationPolicy = new(HomeAccess: HomeAccess.Disabled)
        }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId, await Revision(), draft.Revision);
        var configuration = ConfigurationServices(f).Configuration;
        await configuration.AdoptVersionAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, 2, await Revision(), 1);
        var current = (await authority.CurrentDefinitionAsync(s.Run))!;
        Assert.Equal(2, current.Version); Assert.Equal(removeProfile, RolePermissions.AllowsTool(current, ToolCatalog.SandboxExec));
        Assert.Equal(!removeProfile, RolePermissions.AllowsTool(current, ToolCatalog.WorkspaceRead)); Assert.Equal(HomeAccess.Disabled, current.StateIsolationPolicy!.HomeAccess);
        var saved = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(s.Tenant, s.Run.AgentRunId))!;
        Assert.Equal(1, saved.Admission.Configuration!.Definition.Version);
        Assert.True(RolePermissions.AllowsTool(saved.Admission.Configuration.Definition, ToolCatalog.SandboxExec));
        Assert.False(RolePermissions.AllowsTool(saved.Admission.Configuration.Definition, ToolCatalog.WorkspaceRead));
        Assert.NotEqual(current.AgentInstructions, saved.Admission.Configuration.Definition.AgentInstructions);
        var write = s.Write with { SecurityRevision = await Revision() };
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => ((ITenantSandboxRuntimeStore)f.Iam).TryReserveTenantSandboxLeaseAsync(write, s.Lease, 0, DateTimeOffset.UtcNow).AsTask());
        Assert.Equal("sandbox_policy_changed", error.ReasonCode);
    }
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task Tenant_Sandbox_reservation_rechecks_live_limits_and_home_policy_despite_refreshed_security_witness(bool sqlite, bool home)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f); var store = (ITenantSandboxRuntimeStore)f.Iam;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var mounted = new SandboxMountAuthority("working-inode", "home-inode", HomeAccess.ReadWrite, 1000, 1000);
        var lease = s.Lease with { MountAuthority = mounted };
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(s.Write, lease, 0, DateTimeOffset.UtcNow));
        Assert.True(await store.TrySaveTenantSandboxLeaseFactsAsync(s.Tenant, lease with { Revision = 2, State = SandboxLeaseState.Ready }, 1));
        var profiles = SetupProfiles(f);
        var config = ConfigurationServices(f, sandboxImages: profiles.Images, sandboxReadiness: profiles.Ready).Configuration;
        var changed = home
            ? await config.PatchSettingsAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, "stateIsolationPolicy", await Revision(), 1,
                Fields(new { homeAccess = "Disabled" }), [], initiator: s.Human)
            : await config.PatchSettingsAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, "sandboxPolicy", await Revision(), 1,
                Fields(new { cpu = .5 }), [], initiator: s.Human);
        var write = s.Write with { SecurityRevision = await Revision() };
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxExecutionAsync(write, RuntimeExecution(f, s), DateTimeOffset.UtcNow).AsTask());
        Assert.Equal(home ? "sandbox_mount_policy_changed" : "sandbox_setup_changed", denied.ReasonCode);
        Assert.Null((await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Execution);
        Assert.Equal(2, (await store.ReadTenantSandboxRuntimeAsync(s.Tenant, s.Session.SessionId))!.Lease!.Revision);
        await config.PatchSettingsAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, home ? "stateIsolationPolicy" : "sandboxPolicy",
            await Revision(), changed.Instance.Revision, Fields(new { }), [home ? "homeAccess" : "cpu"], initiator: s.Human);
        Assert.True(await store.TryReserveTenantSandboxExecutionAsync(s.Write with { SecurityRevision = await Revision() }, RuntimeExecution(f, s), DateTimeOffset.UtcNow));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_Sandbox_current_home_relaxation_cannot_expand_the_admitted_Run_mount(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var s = await PrepareRuntimeSandbox(f, home: HomeAccess.Disabled);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision;
        var draft = await f.Service.ForkAsync(s.Human.PrincipalId, s.Tenant, Guid.Parse(s.Run.DefinitionId), 1, await Revision());
        draft = await f.Service.UpdateAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId,
            draft.Candidate with { StateIsolationPolicy = new(HomeAccess: HomeAccess.ReadWrite) }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(s.Human.PrincipalId, s.Tenant, draft.DraftId, await Revision(), draft.Revision);
        await ConfigurationServices(f).Configuration.AdoptVersionAsync(s.Human.PrincipalId, s.Tenant, s.Session.AgentInstanceId, 2, await Revision(), 1);
        var write = s.Write with { SecurityRevision = (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision };
        var store = (ITenantSandboxRuntimeStore)f.Iam;
        var broad = s.Lease with { MountAuthority = new("working", "home", HomeAccess.ReadWrite, 1000, 1000) };
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryReserveTenantSandboxLeaseAsync(write, broad, 0, DateTimeOffset.UtcNow).AsTask());
        Assert.Equal("sandbox_mount_policy_changed", denied.ReasonCode);
        Assert.True(await store.TryReserveTenantSandboxLeaseAsync(write,
            s.Lease with { MountAuthority = new("working", null, HomeAccess.Disabled, 1000, 1000) }, 0, DateTimeOffset.UtcNow));
    }

}
