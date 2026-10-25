using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Space_private_provisioning_membership_CAS_tenant_isolation_and_reopen_share_one_owner(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("space-owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Space agent", await Revision());
        var publication = await f.Service.PublishAsync(actor, tenant, draft.DraftId, await Revision(), draft.Revision);
        var builtIns = new SessionNoBuiltinFallback(); var directory = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, builtIns, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var agent = await directory.CreateFromPublicationAsync(actor, tenant, Guid.Parse(publication.DefinitionId), publication.Version, await Revision());
        var store = (ITenantSpaceStore)f.Iam;
        var home = Assert.IsType<Space>(await store.FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId));
        Assert.Equal(SpaceKind.Private, home.Kind); Assert.False(home.EnvironmentsEnabled);
        var privateMember = Assert.Single(await store.ReadMembershipsAsync(tenant, home.SpaceId));
        Assert.Equal(agent.Instance.InstanceId, privateMember.InstanceId); Assert.Empty(privateMember.AddedBindings);
        var resolver = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns, new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam);
        var service = new TenantSpaceService(f.Iam, store, (ITenantAgentStore)f.Iam, f.Authorizer, resolver, (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var ownInstructions = await service.SetMemberAsync(actor, tenant, home.SpaceId, agent.Instance.InstanceId, "Owner", "Private situational instructions", [], [],
            await Revision(), home.Revision, privateMember.Revision);
        home = await service.ReadAsync(actor, tenant, home.SpaceId);
        var privateRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SetMemberAsync(actor, tenant, home.SpaceId, agent.Instance.InstanceId, "", "", [], [],
            privateRevision, home.Revision, ownInstructions.Revision, remove: true).AsTask());
        var shared = await service.CreateAsync(actor, tenant, f.Ids.NewId(), "Review room", "Shared files", "Review with care", await Revision());
        Assert.Equal(SpaceKind.Shared, shared.Kind); Assert.Null(shared.OwnerInstanceId); Assert.False(shared.EnvironmentsEnabled);
        Assert.Equal(2, (await service.ListAsync(actor, tenant)).Total);
        var member = await service.SetMemberAsync(actor, tenant, shared.SpaceId, agent.Instance.InstanceId, "Reviewer", "Check security", [], [], await Revision(), shared.Revision, 0);
        Assert.Equal(1, member.Revision); Assert.Equal("Check security", Assert.Single(await service.MembersAsync(actor, tenant, shared.SpaceId)).Instructions);
        var current = (await service.ReadAsync(actor, tenant, shared.SpaceId));
        Assert.Equal(2, current.Revision);
        var revision = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.SetMemberAsync(actor, tenant, shared.SpaceId, agent.Instance.InstanceId, "", "stale", [], [], revision, shared.Revision, member.Revision).AsTask());
        Assert.Equal(revision, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.DeleteAsync(actor, tenant, home.SpaceId, revision, home.Revision).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(async () => await service.DeleteAsync(actor, tenant, shared.SpaceId, revision, current.Revision).AsTask());
        Assert.Equal(revision, await Revision());
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        Assert.Null(await store.FindSpaceAsync(beta.Tenant.TenantId, shared.SpaceId));
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.ReadAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, shared.SpaceId).AsTask());
        var foreignScope = TenantSpaceService.Scope(beta.Tenant.TenantId, shared.SpaceId);
        Assert.False((await f.Authorizer.CheckAsync(beta.Users.Single().PrincipalId, "spaces.read", foreignScope)).Allowed);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.SetMemberAsync(actor, tenant, shared.SpaceId, Guid.NewGuid(), "", "", [], [], await Revision(), current.Revision, 0).AsTask());
        Assert.Equal(revision, await Revision());
        var role = await f.Access.SaveRoleAsync(actor, tenant, null, "One Space reader", ["spaces.read"], await Revision());
        var grant = await f.Access.AssignRoleAsync(actor, tenant, agent.PrincipalId, role.RoleId, TenantSpaceService.Scope(tenant, shared.SpaceId), await Revision());
        var visible = await service.ListAsync(agent.PrincipalId, tenant); Assert.Equal(shared.SpaceId, Assert.Single(visible.Items).SpaceId);
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.ReadAsync(agent.PrincipalId, tenant, home.SpaceId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(async () => await service.UpdateAsync(agent.PrincipalId, tenant, shared.SpaceId, "denied", "", "", false, await Revision(), current.Revision).AsTask());
        await f.Access.RevokeAssignmentAsync(actor, tenant, grant.AssignmentId, await Revision());
        await service.SetMemberAsync(actor, tenant, shared.SpaceId, agent.Instance.InstanceId, "", "", [], [], await Revision(), current.Revision, member.Revision, remove: true);
        Assert.Empty(await store.ReadMembershipsAsync(tenant, shared.SpaceId));
        current = await service.ReadAsync(actor, tenant, shared.SpaceId);
        await service.DeleteAsync(actor, tenant, shared.SpaceId, await Revision(), current.Revision);
        Assert.Null(await store.FindSpaceAsync(tenant, shared.SpaceId));
        Assert.Contains(await f.Iam.ReadAuditAsync(tenant, 100), a => a.Action.StartsWith("space", StringComparison.Ordinal));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(home, await reopened.FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId));
            Assert.Single(await reopened.ReadMembershipsAsync(tenant, home.SpaceId));
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            await using var command = db.Database.GetDbConnection().CreateCommand(); await db.Database.OpenConnectionAsync();
            command.CommandText = "PRAGMA foreign_key_check"; await using var reader = await command.ExecuteReaderAsync(); Assert.False(await reader.ReadAsync());
        }
    }
}
