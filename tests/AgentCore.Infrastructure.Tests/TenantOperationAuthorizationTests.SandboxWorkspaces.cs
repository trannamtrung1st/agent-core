using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_workspace_mounts_intersect_actual_read_write_delete_IAM_and_reject_revoked_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var s = await Setup(f, "general-assistant", 35);
        async Task<TenantSecurityState> State() => (await f.Iam.ReadTenantAsync(s.Tenant))!;
        async Task<SandboxWorkspaceAccess> Access(HomeAccess policy = HomeAccess.ReadWrite) =>
            await TenantSandboxWorkspaceRules.ResolveAsync(await State(), s.Run, policy, f.Authorizer, default);
        await Grant(f, s, s.Agent.PrincipalId, "Execute only", ["sandbox.execute"]);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Access());
        await GrantWorkspaceRuntimeReads(f, s, ["spaces.read", "sessions.read", "workspaces.read", "workspaces.write"]);
        var readOnly = await Access();
        Assert.Equal(HomeAccess.ReadOnly, readOnly.HomeAccess); Assert.Equal(HomeAccess.ReadOnly, readOnly.PrivateAccess); Assert.True(readOnly.WorkingReadOnly);
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Delete exact files", ["workspaces.delete"], (await State()).Tenant.Revision);
        var homeGrant = await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, TenantWorkspaceRules.Home(s.Tenant, SpaceId(s)), (await State()).Tenant.Revision);
        var homeWrite = await Access();
        Assert.Equal(HomeAccess.ReadWrite, homeWrite.HomeAccess); Assert.Equal(HomeAccess.ReadWrite, homeWrite.PrivateAccess); Assert.True(homeWrite.WorkingReadOnly);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, TenantWorkspaceRules.Working(s.Tenant, s.Run.SessionId), (await State()).Tenant.Revision);
        var bothWrite = await Access(); Assert.False(bothWrite.WorkingReadOnly);
        var policyReadOnly = await Access(HomeAccess.ReadOnly);
        Assert.Equal(HomeAccess.ReadOnly, policyReadOnly.HomeAccess); Assert.Equal(HomeAccess.ReadOnly, policyReadOnly.PrivateAccess); Assert.False(policyReadOnly.WorkingReadOnly);
        var policyDisabled = await Access(HomeAccess.Disabled);
        Assert.Equal(HomeAccess.Disabled, policyDisabled.HomeAccess); Assert.Equal(HomeAccess.Disabled, policyDisabled.PrivateAccess);
        await f.Access.RevokeAssignmentAsync(s.Owner.PrincipalId, s.Tenant, homeGrant.AssignmentId, (await State()).Tenant.Revision);
        var revoked = await Access(); Assert.Equal(HomeAccess.ReadOnly, revoked.HomeAccess); Assert.False(revoked.WorkingReadOnly);
        Assert.Throws<AccessDeniedException>(() => TenantSandboxWorkspaceRules.Require(bothWrite, revoked));
        Assert.Equal(TenantWorkspaceRules.Context(s.Run), revoked.Context);
    }
}
