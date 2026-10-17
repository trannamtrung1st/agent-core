using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantRunAdmissionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Agent_access_separates_chat_audience_from_executor_permissions_and_paginates_safe_choices(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var service = new TenantAgentAccessService(f.Iam, (ITenantAgentStore)f.Iam, f.Identity, f.Authorizer);
        var chat = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Talk to this agent", ["agents.chat"], await Revision());
        var assignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, chat.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var view = await service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, q: "Requester");
        Assert.Equal(member.PrincipalId, Assert.Single(view.Principals.Items).PrincipalId);
        Assert.Equal(1, view.Principals.Total);
        Assert.True(Assert.Single(view.Audience.Items, a => a.AssignmentId == assignment.AssignmentId).EffectiveChat);
        Assert.DoesNotContain(view.Permissions, p => p.Allowed);
        Assert.True(view.CanGrantChat); Assert.True(view.CanGrantExecution); Assert.True(view.CanInspectOrganization);
        var execution = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Read credential metadata", ["credentials.read"], await Revision());
        var executorAssignment = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.PrincipalId, execution.RoleId, AccessScope.ForTenant(setup.Tenant), await Revision());
        view = await service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, authorityScope: "tenant");
        Assert.Contains(view.Execution.Items, a => a.AssignmentId == executorAssignment.AssignmentId && a.Scope.Kind == AccessScopeKind.Tenant);
        Assert.True(Assert.Single(view.Permissions, p => p.Key == "credentials.read").Allowed);
        Assert.All(view.Permissions.Where(p => p.HumanOnly), p => { Assert.False(p.Allowed); Assert.Equal("human_only", p.ReasonCode); });
        Assert.DoesNotContain(view.ExecutionRoles, r => r.HumanOnly);
        Assert.False((await f.Authorizer.CheckAsync(member.PrincipalId, "credentials.read", AccessScope.ForTenant(setup.Tenant))).Allowed);
        Assert.Empty((await service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner, audiencePage: 1, principalPage: 1)).Audience.Items);
        var json = JsonSerializer.Serialize(view);
        foreach (var forbidden in new[] { "PasswordHash", "SecurityStamp", "LoginKey", "strong-owner-password", "strong-requester-password" }) Assert.DoesNotContain(forbidden, json);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, await Revision());
        Assert.DoesNotContain((await service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner)).Audience.Items, a => a.PrincipalId == member.PrincipalId);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Scoped_agent_access_reader_does_not_acquire_organization_directory_or_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var member = await Member(f, setup.Tenant, setup.Owner.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var service = new TenantAgentAccessService(f.Iam, (ITenantAgentStore)f.Iam, f.Identity, f.Authorizer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, member).AsTask());
        var reader = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, "Scoped access reader", ["agents.read", "iam.roles.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, member.PrincipalId, reader.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), await Revision());
        var view = await service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, member);
        Assert.False(view.CanGrantChat); Assert.False(view.CanGrantExecution); Assert.False(view.CanInspectOrganization); Assert.Empty(view.Principals.Items);
        Assert.All(view.Audience.Items, a => Assert.False(a.CanRevoke));
        Assert.Empty(view.ExecutionRoles); Assert.Empty(view.ChatRoles);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, member, authorityScope: "tenant").AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, Guid.NewGuid(), member).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(Guid.NewGuid(), setup.Agent.Instance.InstanceId, member).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, member, audiencePage: -1).AsTask());
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, grant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, member).AsTask());
        await f.Identity.SignOutAsync(setup.Owner.AuthenticationSessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(setup.Tenant, setup.Agent.Instance.InstanceId, setup.Owner).AsTask());
    }
}
