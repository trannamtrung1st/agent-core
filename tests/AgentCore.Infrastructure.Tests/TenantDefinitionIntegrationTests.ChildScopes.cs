using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_child_scope_assignment_rechecks_suspended_account_at_commit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("access-admin@example.test", "Access admin", "strong-access-admin-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "access-administrator").RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var local = await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, agent.Instance.InstanceId, "create", await Revision(), 1,
            input: new("Scoped review", "Review totals", "Compare totals.", SkillProjection.OnDemand, true, ["chat.respond"]));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var grant = new RoleAssignment(f.Ids.NewId(), owner.TenantId, agent.PrincipalId, state.Roles.Single(r => r.Key == "reader").RoleId,
            TenantAgentChildScopes.Scope(owner.TenantId, agent.Instance.InstanceId, "skill", local.Skill.Key), user.PrincipalId, DateTimeOffset.UtcNow);
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Assignments = state.Assignments.Append(grant).ToArray() };
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Iam.TrySaveTenantAsync(next, state.Tenant.Revision,
            new(f.Ids.NewId(), owner.TenantId, user.PrincipalId, "iam.roles.assign", grant.Scope.ResourceId!, "allowed", "prepared", DateTimeOffset.UtcNow)).AsTask());
        Assert.Equal("account_unavailable", denied.ReasonCode); Assert.Equal(state.Tenant.Revision, await Revision());
        Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments, a => a.AssignmentId == grant.AssignmentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_child_scopes_resolve_current_publication_and_retire_removed_inheritance_across_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Scoped inheritance", await Revision());
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision,
            [new(null, "reference.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Private inherited bytes."))]);
        var source = new SkillSpec("inherited-review", "Inherited review", "Read inherited procedure", "Private inherited procedure.", SkillProjection.OnDemand, true, ["chat.respond"], ["reference.txt"]);
        draft = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, draft.Candidate with { Skills = [source] }, await Revision(), bound.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var definitionId = Guid.Parse(draft.DefinitionId);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision()); var id = agent.Instance.InstanceId;
        var reader = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Roles.Single(r => r.Key == "reader").RoleId;
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), await Revision());
        var skillScope = TenantAgentChildScopes.Scope(owner.TenantId, id, "skill", "definition:inherited-review");
        var resourceKey = "definition:" + Assert.Single(bound.Resources).ResourceId.ToString("D");
        var fileScope = TenantAgentChildScopes.Scope(owner.TenantId, id, "resource", resourceKey);
        var fileGrant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, fileScope, await Revision());
        Assert.Empty(Assert.Single((await configuration.ReadResourcesAsync(agent.PrincipalId, owner.TenantId, id)).Catalog.Resources).Dependencies);
        var skillGrant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, skillScope, await Revision());
        Assert.Equal("definition:inherited-review", Assert.Single((await configuration.ReadSkillsAsync(agent.PrincipalId, owner.TenantId, id)).Items).Key);
        Assert.Equal("definition:inherited-review", Assert.Single(Assert.Single((await configuration.ReadResourcesAsync(agent.PrincipalId, owner.TenantId, id)).Catalog.Resources).Dependencies));
        Assert.Equal("Private inherited bytes.", Encoding.UTF8.GetString((await configuration.ReadResourceContentAsync(agent.PrincipalId, owner.TenantId, id, resourceKey)).Bytes));
        Assert.Equal("resource_type_mismatch", (await f.Authorizer.CheckAsync(agent.PrincipalId, "resources.read", skillScope)).ReasonCode);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        var foreign = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        Assert.False(await f.Iam.OwnsResourceAsync(fileScope with { TenantId = foreign.Tenant.TenantId }));
        var foreignState = (await f.Iam.ReadTenantAsync(foreign.Tenant.TenantId))!;
        var fabricated = fileGrant with { AssignmentId = f.Ids.NewId(), TenantId = foreign.Tenant.TenantId, PrincipalId = foreign.Users.Single().PrincipalId,
            RoleId = foreignState.Roles.Single(r => r.Key == "reader").RoleId, Scope = fileScope with { TenantId = foreign.Tenant.TenantId }, CreatedByPrincipalId = foreign.Users.Single().PrincipalId };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(foreignState with { Tenant = foreignState.Tenant with { Revision = foreignState.Tenant.Revision + 1 }, Assignments = foreignState.Assignments.Append(fabricated).ToArray() },
            foreignState.Tenant.Revision, f.Audit(foreign.Tenant.TenantId, foreign.Users.Single().PrincipalId, id)).AsTask());
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision());
        fork = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, fork.Candidate with { Skills = [new SkillSpec("future-private", "Future private procedure", "Future task", "Private future body", SkillProjection.OnDemand, true, ["chat.respond"], [])] }, await Revision(), fork.Revision);
        var removed = await f.Service.RemoveResourceAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, bound.Resources[0].ResourceId, await Revision(), fork.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), removed.Revision);
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.PreviewVersionAsync(agent.PrincipalId, owner.TenantId, id, 2).AsTask());
        var definitionGrant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader,
            AccessScope.ForResource(owner.TenantId, "definition", definitionId.ToString("D")), await Revision());
        var ownerPreview = await configuration.PreviewVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2);
        Assert.Contains("definition:future-private", ownerPreview.Preview.NewInheritedItems);
        var limitedPreview = await configuration.PreviewVersionAsync(agent.PrincipalId, owner.TenantId, id, 2);
        Assert.False(limitedPreview.CanAdopt); Assert.False(limitedPreview.ChildAccess.AllSkills); Assert.False(limitedPreview.ChildAccess.AllResources);
        Assert.Empty(limitedPreview.Preview.NewInheritedItems);
        Assert.Contains("definition:inherited-review", limitedPreview.Preview.RemovedInheritedItems); Assert.Contains(resourceKey, limitedPreview.Preview.RemovedInheritedItems);
        await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, definitionGrant.AssignmentId, await Revision());
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.ReadVersionsAsync(agent.PrincipalId, owner.TenantId, id).AsTask());
        await configuration.AdoptVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2, await Revision(), 1);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.DoesNotContain(state.Assignments, a => a.AssignmentId == skillGrant.AssignmentId || a.AssignmentId == fileGrant.AssignmentId);
        Assert.False(await f.Iam.OwnsResourceAsync(skillScope)); Assert.False(await f.Iam.OwnsResourceAsync(fileScope));
        // Dormant inherited state is retained, but it is not a current IAM target.
        Assert.Contains((await ((ITenantAgentStore)f.Iam).ReadTenantAgentConfigurationAsync(owner.TenantId, id))!.Skills.DefinitionStates, s => s.DefinitionSkillId == source.Id);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.False(await reopened.OwnsResourceAsync(skillScope));
            Assert.DoesNotContain((await reopened.ReadTenantAsync(owner.TenantId))!.Assignments, a => a.AssignmentId == skillGrant.AssignmentId || a.AssignmentId == fileGrant.AssignmentId);
        }
        var restored = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision());
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, restored.DraftId, await Revision(), restored.Revision);
        await configuration.AdoptVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 3, await Revision(), 2);
        Assert.True(await f.Iam.OwnsResourceAsync(skillScope)); Assert.True(await f.Iam.OwnsResourceAsync(fileScope));
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.ReadSkillsAsync(agent.PrincipalId, owner.TenantId, id).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.ReadResourceContentAsync(agent.PrincipalId, owner.TenantId, id, resourceKey).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_child_scope_delete_and_grant_share_atomic_security_witnesses(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1); var id = agent.Instance.InstanceId;
        var store = (ITenantAgentStore)f.Iam;
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        async ValueTask<long> Ir() => (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision;
        var input = new InstanceSkillInput("Scoped local", "Review totals", "Compare totals.", SkillProjection.OnDemand, true, ["chat.respond"], "scoped-local");
        var created = await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "create", await Revision(), await Ir(), input: input);
        var scope = TenantAgentChildScopes.Scope(owner.TenantId, id, "skill", created.Skill.Key);
        var reader = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Roles.Single(r => r.Key == "reader").RoleId;
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, scope, await Revision());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }; var now = DateTimeOffset.UtcNow;
        AuthorizationAudit Audit(string action) => new(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, action, scope.ResourceId!, "allowed", "test", now);
        var deleting = new TenantAgentConfigurationChange(id, await Ir(), Skill: new(id, await Ir(), DeleteSkillId: "scoped-local", ExpectedSkillRevision: 1));
        var auditCount = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, deleting, now, Audit("skills.delete")).AsTask());
        Assert.Equal(state.Tenant.Revision, await Revision()); Assert.Equal(auditCount, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.True(await f.Iam.OwnsResourceAsync(scope));
        await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "delete", await Revision(), await Ir(), created.Skill.Key, 1);
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments, a => a.AssignmentId == grant.AssignmentId);
        await configuration.WriteSkillAsync(owner.OwnerPrincipalId, owner.TenantId, id, "create", await Revision(), await Ir(), input: input);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "skills.read", scope)).Allowed);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var assignment = grant with { AssignmentId = f.Ids.NewId() };
        deleting = deleting with { ExpectedInstanceRevision = await Ir(), Skill = deleting.Skill! with { ExpectedInstanceRevision = await Ir() } };
        auditCount = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var granting = f.Iam.TrySaveTenantAsync(next with { Assignments = next.Assignments.Append(assignment).ToArray() }, state.Tenant.Revision, Audit("iam.roles.assign")).AsTask();
        var removing = store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, deleting, now, Audit("skills.delete")).AsTask();
        await Task.WhenAll(granting, removing);
        Assert.NotEqual(await granting, await removing is not null);
        Assert.Equal(state.Tenant.Revision + 1, await Revision()); Assert.Equal(auditCount + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(await granting, await f.Iam.OwnsResourceAsync(scope));
        Assert.Equal(await granting, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments.Any(a => a.AssignmentId == assignment.AssignmentId));
    }
}
