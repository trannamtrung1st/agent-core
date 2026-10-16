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
    public async Task Definition_resource_grants_retire_atomically_on_publication_draft_and_root_deletion(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Scoped resources", await Revision()); var definition = Guid.Parse(draft.DefinitionId);
        var bytes = Encoding.UTF8.GetBytes("Private Definition bytes.");
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 1,
            [new(null, "visible.txt", AgentDefinitionResourceKind.Reference, "text/plain", bytes), new(null, "hidden.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65])]);
        var resource = bound.Resources.Single(r => r.LogicalPath == "visible.txt");
        var agent = await ConfigurationServices(f).Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var reader = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Roles.Single(r => r.Key == "reader").RoleId;
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, AccessScope.ForResource(owner.TenantId, "definition", draft.DefinitionId), await Revision());
        var scope = TenantDefinitionResourceScopes.Draft(owner.TenantId, definition, draft.DraftId, resource.ResourceId);
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, scope, await Revision());
        var limited = await f.Service.ReadDraftStateAsync(agent.PrincipalId, owner.TenantId, draft.DraftId);
        Assert.Equal(resource.ResourceId, Assert.Single(limited.Resources).ResourceId); Assert.False(limited.ResourceAccess.AllRead); Assert.False(limited.ResourceAccess.CanCreate);
        var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<ArgumentException>(() => f.Definitions.TryRemoveDefinitionResourceAsync(next, before.Tenant.Revision, draft.DraftId, 2, resource.ResourceId, f.Audit(owner.TenantId, owner.OwnerPrincipalId, definition)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Definitions.TryPublishDefinitionDraftAsync(next, before.Tenant.Revision, draft.DraftId, 2, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, definition)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Definitions.TryDeleteDefinitionDraftAsync(next, before.Tenant.Revision, draft.DraftId, 2, true, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, definition)).AsTask());
        Assert.Equal(before.Tenant.Revision, await Revision()); Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(2, (await f.Definitions.ReadDefinitionDraftSnapshotAsync(owner.TenantId, draft.DraftId))!.Resources.Count);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 2);
        Assert.False(await f.Iam.OwnsResourceAsync(scope)); Assert.DoesNotContain((await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments, a => a.AssignmentId == grant.AssignmentId);
        var publicationScope = TenantDefinitionResourceScopes.Publication(owner.TenantId, definition, 1, resource.ResourceId);
        var publishedGrant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, publicationScope, await Revision());
        Assert.Equal(bytes, (await f.Service.ReadPublicationResourceAsync(agent.PrincipalId, owner.TenantId, definition, 1, resource.ResourceId)).Content);
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, definition, 1, await Revision());
        Assert.Empty((await f.Service.ReadDraftStateAsync(agent.PrincipalId, owner.TenantId, fork.DraftId)).Resources);
        var forkScope = TenantDefinitionResourceScopes.Draft(owner.TenantId, definition, fork.DraftId, resource.ResourceId);
        var forkGrant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, forkScope, await Revision());
        await f.Service.DeleteDraftAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), 1);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.DoesNotContain(state.Assignments, a => a.AssignmentId == forkGrant.AssignmentId); Assert.Contains(state.Assignments, a => a.AssignmentId == publishedGrant.AssignmentId);
        await f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, definition, new([], [new(1, 1)]), await Revision());
        Assert.False(await f.Iam.OwnsResourceAsync(publicationScope)); Assert.Equal(bytes, await f.Content.ReadAsync(resource.ContentSha256));
        Assert.DoesNotContain((await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments, a => TenantDefinitionResourceScopes.BelongsTo(a.Scope, definition));
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.False(await reopened.OwnsResourceAsync(publicationScope)); Assert.False(await reopened.OwnsResourceAsync(forkScope));
            Assert.DoesNotContain((await reopened.ReadTenantAsync(owner.TenantId))!.Assignments, a => TenantDefinitionResourceScopes.BelongsTo(a.Scope, definition));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definition_resource_scope_grant_delete_races_and_foreign_references_keep_one_owned_graph(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Resource race", await Revision()); var definition = Guid.Parse(draft.DefinitionId);
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 1, [new(null, "race.txt", AgentDefinitionResourceKind.Reference, "text/plain", [65])]);
        var file = Assert.Single(bound.Resources); var scope = TenantDefinitionResourceScopes.Draft(owner.TenantId, definition, draft.DraftId, file.ResourceId);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        Assert.False(await f.Iam.OwnsResourceAsync(scope with { TenantId = beta.Tenant.TenantId }));
        Assert.False(await f.Iam.OwnsResourceAsync(TenantDefinitionResourceScopes.Draft(owner.TenantId, Guid.NewGuid(), draft.DraftId, file.ResourceId)));
        Assert.Throws<AccessDeniedException>(() => IamResourceScopes.Create(owner.TenantId, "resource", $"{definition:D}:publication:01:{file.ResourceId:D}"));
        var recipient = await ConfigurationServices(f).Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var now = DateTimeOffset.UtcNow;
        var grant = new RoleAssignment(f.Ids.NewId(), owner.TenantId, recipient.PrincipalId, state.Roles.Single(r => r.Key == "reader").RoleId, scope, owner.OwnerPrincipalId, now);
        var betaState = (await f.Iam.ReadTenantAsync(beta.Tenant.TenantId))!;
        var forged = grant with { TenantId = beta.Tenant.TenantId, PrincipalId = beta.Users.Single().PrincipalId, RoleId = betaState.Roles.Single(r => r.Key == "reader").RoleId, Scope = scope with { TenantId = beta.Tenant.TenantId } };
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(betaState with { Tenant = betaState.Tenant with { Revision = betaState.Tenant.Revision + 1 }, Assignments = betaState.Assignments.Append(forged).ToArray() },
            betaState.Tenant.Revision, f.Audit(beta.Tenant.TenantId, beta.Users.Single().PrincipalId, definition)).AsTask());
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }; var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var assigning = f.Iam.TrySaveTenantAsync(next with { Assignments = next.Assignments.Append(grant).ToArray() }, state.Tenant.Revision, f.Audit(owner.TenantId, owner.OwnerPrincipalId, definition)).AsTask();
        var deleting = f.Definitions.TryRemoveDefinitionResourceAsync(next, state.Tenant.Revision, draft.DraftId, 2, file.ResourceId, f.Audit(owner.TenantId, owner.OwnerPrincipalId, definition)).AsTask();
        await Task.WhenAll(assigning, deleting);
        Assert.NotEqual(await assigning, await deleting is not null); Assert.Equal(state.Tenant.Revision + 1, await Revision());
        Assert.Equal(count + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(await assigning, await f.Iam.OwnsResourceAsync(scope)); Assert.Equal(await assigning, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Assignments.Any(a => a.AssignmentId == grant.AssignmentId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Definition_resource_download_rechecks_exact_grant_and_binding_after_private_io(bool sqlite, bool replace)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Download authority", await Revision()); var definition = Guid.Parse(draft.DefinitionId);
        var bound = await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 1, [new(null, "private.txt", AgentDefinitionResourceKind.Reference, "text/plain", Encoding.UTF8.GetBytes("Original bytes."))]);
        var file = Assert.Single(bound.Resources); var agent = await ConfigurationServices(f).Agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, await Revision());
        var reader = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Roles.Single(r => r.Key == "reader").RoleId;
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, AccessScope.ForResource(owner.TenantId, "definition", draft.DefinitionId), await Revision());
        var grant = await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, reader, TenantDefinitionResourceScopes.Draft(owner.TenantId, definition, draft.DraftId, file.ResourceId), await Revision());
        var gated = new GatedInstanceContent(f.Content); var service = f.WithTemplates(Fixture.ShippedTemplates(), resourceContent: gated);
        var pending = service.ReadDraftResourceAsync(agent.PrincipalId, owner.TenantId, draft.DraftId, file.ResourceId).AsTask();
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            if (replace) await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), 2,
                [new(file.ResourceId, file.LogicalPath, file.Kind, file.MediaType, Encoding.UTF8.GetBytes("Replacement bytes."))]);
            else await f.Access.RevokeAssignmentAsync(owner.OwnerPrincipalId, owner.TenantId, grant.AssignmentId, await Revision());
        }
        finally { gated.Release.TrySetResult(); }
        if (replace) Assert.Equal("resource_changed", (await Assert.ThrowsAsync<SecurityConflictException>(() => pending)).ReasonCode);
        else await Assert.ThrowsAsync<AccessDeniedException>(() => pending);
        Assert.Equal(replace ? "Replacement bytes." : "Original bytes.", Encoding.UTF8.GetString((await f.Service.ReadDraftResourceAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, file.ResourceId)).Content));
    }
}
