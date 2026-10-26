using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deleting_last_unpublished_draft_purges_children_and_grants_without_reopening_global_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Disposable draft", 1);
        var id = Guid.Parse(draft.DefinitionId);
        var bytes = "Private reference"u8.ToArray(); var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await f.Content.StoreVerifiedAsync(hash, bytes);
        var resource = new AgentDefinitionDraftResource(f.Ids.NewId(), draft.DraftId, "private.txt", AgentDefinitionResourceKind.Reference, "text/plain", hash, bytes.Length, DateTimeOffset.UtcNow);
        var scenario = new DefinitionEvaluationScenario(draft.DraftId, "required", 1, "Required", "Check", DefinitionEvaluationRequirementLevel.Required, DefinitionEvaluationCheckType.ExternalActionDenied, null, DateTimeOffset.UtcNow);
        await f.SeedEvidence(draft, resource, scenario, new(draft.DraftId, 1, "old-evidence", scenario.ScenarioId, 1, "Synthetic", true, [], DateTimeOffset.UtcNow));
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var grant = new RoleAssignment(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, state.Roles.Single(r => r.Key == "reader").RoleId,
            AccessScope.ForResource(owner.TenantId, "definition", draft.DefinitionId), owner.OwnerPrincipalId, DateTimeOffset.UtcNow);
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = 3 }, Assignments = state.Assignments.Append(grant).ToArray() }, 2, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        var before = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteDraftAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 3, 2).AsTask());
        Assert.NotNull(await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId));
        await f.Service.DeleteDraftAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 3, 1);
        Assert.Null(await f.Definitions.FindDefinitionDraftAsync(owner.TenantId, draft.DraftId));
        Assert.Null(await f.Definitions.ReadDefinitionDraftSnapshotAsync(owner.TenantId, draft.DraftId));
        Assert.Null(await f.Definitions.FindDefinitionAsync(owner.TenantId, id));
        Assert.Equal(0, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        Assert.False(await f.Iam.OwnsResourceAsync(grant.Scope));
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        Assert.Equal(4, state.Tenant.Revision); Assert.DoesNotContain(state.Assignments, a => a.AssignmentId == grant.AssignmentId);
        Assert.Equal(before + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Admin.CreateDraftAsync(new(draft.DefinitionId, draft.Candidate,
            DefinitionDraftSourceKind.New, null, DateTimeOffset.UtcNow)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = 5 }, Assignments = state.Assignments.Append(grant).ToArray() }, 4,
            f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask());
        // The root remains an exclusion marker; child bindings/evidence are physically removed.
        await AssertPurged(f, draft.DefinitionId, [draft.DraftId]);
        if (f.Contexts is { } contexts)
        {
            await using var db = contexts.CreateDbContext();
            Assert.NotNull((await db.Set<TenantDefinitionRecord>().SingleAsync(d => d.DefinitionId == draft.DefinitionId)).DeletedAt);
            Assert.Null(await new SqliteIamStore(contexts, f.Content).FindDefinitionAsync(owner.TenantId, id));
        }
        else Assert.NotNull(((InMemoryAgentDefinitionAdminStore)f.Admin).TenantDefinitions[id].DeletedAt);
        // Immutable content may be shared; deleting one binding never erases another owner's bytes.
        Assert.Equal(bytes, await f.Content.ReadAsync(hash));
    }

    private static async Task AssertPurged(Fixture f, string definitionId, Guid[] draftIds)
    {
        if (f.Contexts is { } contexts)
        {
            await using var db = contexts.CreateDbContext(); var keys = draftIds.Select(id => id.ToString("D")).ToArray();
            Assert.False(await db.AgentDefinitionDrafts.IgnoreQueryFilters().AnyAsync(d => d.DefinitionId == definitionId));
            Assert.False(await db.AgentDefinitionPublications.IgnoreQueryFilters().AnyAsync(p => p.DefinitionId == definitionId));
            Assert.False(await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AnyAsync(r => keys.Contains(r.DraftId)));
            Assert.False(await db.AgentDefinitionPublicationResources.IgnoreQueryFilters().AnyAsync(r => r.DefinitionId == definitionId));
            Assert.False(await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().AnyAsync(s => keys.Contains(s.DraftId)));
            Assert.False(await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().AnyAsync(r => keys.Contains(r.DraftId)));
        }
        else
        {
            var admin = (InMemoryAgentDefinitionAdminStore)f.Admin;
            foreach (var draftId in draftIds)
            {
                Assert.Null(admin.ResourceStore!.CopyDraftResources(draftId));
                Assert.Empty(await f.Evaluation.ListScenariosAsync(draftId)); Assert.Empty(await f.Evaluation.ListResultsAsync(draftId));
            }
            Assert.Null(admin.ResourceStore!.CopyPublicationResources(definitionId, 1));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definition_deletion_uses_all_revision_witnesses_and_commits_one_competitor_without_touching_siblings(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Published", 1); var id = Guid.Parse(draft.DefinitionId);
        await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1,
            [new(null, "shared.txt", AgentDefinitionResourceKind.Reference, "text/plain", "Shared bytes"u8.ToArray())]);
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 3, 2);
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, 4);
        var anotherFork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, 5);
        var sibling = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Keep this", 6);
        await f.Service.BindResourcesAsync(owner.OwnerPrincipalId, owner.TenantId, sibling.DraftId, 7, 1,
            [new(null, "shared.txt", AgentDefinitionResourceKind.Reference, "text/plain", "Shared bytes"u8.ToArray())]);
        var witness = new AdminDefinitionDeleteWitness([new(fork.DraftId, 1), new(anotherFork.DraftId, 1)], [new(1, 1)]);
        // Remove only one fork; keep the publication, other draft and all publication bytes.
        await f.Service.DeleteDraftAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, 8, 1);
        Assert.NotNull(await f.Definitions.FindDefinitionAsync(owner.TenantId, id));
        Assert.Single(await f.Definitions.ListOwnedPublicationResourcesAsync(owner.TenantId, id, 1));
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, witness, 9).AsTask());
        witness = new([new(anotherFork.DraftId, 1)], [new(1, 1)]);
        await f.Service.DeprecateAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, 9, 1);
        // A metadata change also invalidates the reviewed deletion, even with current security revision.
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, witness, 10).AsTask());
        witness = witness with { Publications = [new(1, 2)] };
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = TenantDefinitionDeletionRules.WithoutResourceGrants(state with { Tenant = state.Tenant with { Revision = 11 } }, id);
        var auditBefore = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var attempts = await Task.WhenAll(f.Definitions.TryDeleteDefinitionAsync(next, 10, id, witness, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask(),
            f.Definitions.TryDeleteDefinitionAsync(next, 10, id, witness, DateTimeOffset.UtcNow, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask());
        Assert.Single(attempts, result => result is not null);
        Assert.Equal(auditBefore + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await AssertPurged(f, draft.DefinitionId, [draft.DraftId, fork.DraftId, anotherFork.DraftId]);
        var siblingState = await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, sibling.DraftId);
        var siblingResource = Assert.Single(siblingState.Resources);
        Assert.Equal("Shared bytes"u8.ToArray(), (await f.Service.ReadDraftResourceAsync(owner.OwnerPrincipalId, owner.TenantId, sibling.DraftId, siblingResource.ResourceId)).Content);
        Assert.Equal(1, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        Assert.Null(await f.Admin.GetPublicationAsync(draft.DefinitionId, publication.Version));
        if (sqlite) Assert.Null(await new SqliteIamStore(f.Contexts!, f.Content).FindDefinitionPublicationAsync(owner.TenantId, id, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archived_instances_and_retained_session_activations_block_definition_deletion(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Referenced", 1); var id = Guid.Parse(draft.DefinitionId);
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1);
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        var templates = new FileAgentDefinitionStore(Path.Combine(directory!.FullName, "agents"), SyntheticProviderAliases.Default);
        var agents = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, templates, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var instance = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, 3);
        await agents.SetLifecycleAsync(owner.OwnerPrincipalId, owner.TenantId, instance.Instance.InstanceId, AgentInstanceLifecycle.Archived, 4, 1);
        var witness = new AdminDefinitionDeleteWitness([], [new(1, 1)]);
        var auditBefore = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var stale = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id,
            witness with { Publications = [new(1, 2)] }, 5).AsTask());
        Assert.Equal("stale_revision", stale.ReasonCode);
        var error = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, id, witness, 5).AsTask());
        Assert.Equal("definition_in_use", error.ReasonCode);
        Assert.Equal(auditBefore, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(5, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.NotNull(await f.Definitions.FindDefinitionPublicationAsync(owner.TenantId, id, 1));
        Assert.NotNull(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, instance.Instance.InstanceId));

        // A retained activation must protect history even if no Instance remains in a damaged graph.
        var historical = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "History", 5);
        var historicalId = Guid.Parse(historical.DefinitionId);
        var historicalPublication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, historical.DraftId, 6, 1);
        var now = DateTimeOffset.UtcNow; var sessionId = f.Ids.NewId(); var missingInstance = f.Ids.NewId();
        var snapshot = new SessionSnapshot(1, sessionId, 1, historicalPublication.Payload, SessionMode.Text, null, SessionStatus.Created,
            [], string.Empty, 0, null, null, now, now, AgentInstanceId: missingInstance);
        await f.SessionStore.SaveAsync(snapshot, 0);
        error = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, historicalId, witness, 7).AsTask());
        Assert.Equal("definition_in_use", error.ReasonCode);
        var activation = new ActivationRecord { ActivationId = f.Ids.NewId().ToString("D"), SessionId = sessionId.ToString("D"),
            AgentInstanceId = missingInstance.ToString("D"), DedupeKey = "retained-history", AdmissionHash = "test", ProfileId = "", PayloadJson = "{}", AdmittedAtUtc = now.ToUnixTimeMilliseconds() };
        activation.RootActivationId = activation.ActivationId;
        if (f.Contexts is { } contexts)
        {
            await using var db = contexts.CreateDbContext();
            await db.Sessions.Where(s => s.SessionId == sessionId.ToString("D")).ExecuteUpdateAsync(update => update.SetProperty(s => s.DurablyDeletedAtUtc, now.ToUnixTimeMilliseconds()));
            db.Activations.Add(activation); await db.SaveChangesAsync();
        }
        else
        {
            var sessions = (InMemoryMemoryStore)f.SessionStore;
            await sessions.SaveAsync(snapshot with { Revision = 2, DurablyDeletedAt = now }, 1);
            lock (sessions.AdmissionGate) sessions.AgentRuns.Activations[Guid.Parse(activation.ActivationId)] = activation;
        }
        error = await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteAsync(owner.OwnerPrincipalId, owner.TenantId, historicalId, witness, 7).AsTask());
        Assert.Equal("definition_in_use", error.ReasonCode);
        Assert.NotNull(await f.Definitions.FindDefinitionPublicationAsync(owner.TenantId, historicalId, 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_rejects_foreign_targets_stale_security_and_suspended_actor_without_success_audit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single();
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        var account = await f.Identity.RegisterAsync("writer@example.test", "Writer", "strong-writer-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "contributor").RoleId, 1);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var draft = await f.Service.CreateAsync(user.PrincipalId, owner.TenantId, "Preserve", state.Tenant.Revision); var id = Guid.Parse(draft.DefinitionId);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var before = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.DeleteDraftAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, draft.DraftId, beta.Tenant.Revision, 1).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.DeleteAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, id, new([new(draft.DraftId, 1)], []), beta.Tenant.Revision).AsTask());
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Service.DeleteDraftAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, state.Tenant.Revision - 1, 1).AsTask());
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        var admin = new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System);
        await admin.SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Definitions.TryDeleteDefinitionDraftAsync(next, state.Tenant.Revision, draft.DraftId, 1, true,
            DateTimeOffset.UtcNow, f.Audit(owner.TenantId, user.PrincipalId, id)).AsTask());
        Assert.Equal("account_unavailable", error.ReasonCode);
        Assert.NotNull(await f.Definitions.FindDefinitionDraftAsync(owner.TenantId, draft.DraftId));
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(before, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Instance_creation_and_definition_retirement_share_one_atomic_security_witness(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Race", 1); var id = Guid.Parse(draft.DefinitionId);
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; var now = DateTimeOffset.UtcNow;
        var instance = new AgentInstance(f.Ids.NewId(), draft.DefinitionId, 1, publication.Payload.Identity, AgentInstanceLifecycle.Active, now, now);
        var systemId = f.Ids.NewId(); var principalId = f.Ids.NewId();
        var next = state with { Tenant = state.Tenant with { Revision = 4 } };
        var creating = next with
        {
            Principals = next.Principals.Append(new(principalId, PrincipalKind.SystemIdentity, owner.TenantId, null, null, systemId, IdentityStatus.Active)).ToArray(),
            SystemIdentities = next.SystemIdentities.Append(new(systemId, owner.TenantId, principalId, SystemIdentityKind.Agent, instance.InstanceId, IdentityStatus.Active)).ToArray()
        };
        var before = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var create = ((ITenantAgentStore)f.Iam).TryCreateTenantAgentAsync(creating, 3, instance, [], f.Audit(owner.TenantId, owner.OwnerPrincipalId, instance.InstanceId)).AsTask();
        var delete = f.Definitions.TryDeleteDefinitionAsync(next, 3, id, new([], [new(1, 1)]), now, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask();
        await Task.WhenAll(create, delete);
        Assert.NotEqual(await create, await delete is not null);
        Assert.Equal(before + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(4, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        if (await create)
        {
            Assert.NotNull(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, instance.InstanceId));
            Assert.NotNull(await f.Definitions.FindDefinitionPublicationAsync(owner.TenantId, id, 1));
        }
        else
        {
            Assert.Null(await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, instance.InstanceId));
            Assert.DoesNotContain((await f.Iam.ReadTenantAsync(owner.TenantId))!.SystemIdentities, i => i.SystemIdentityId == systemId);
            Assert.Null(await f.Definitions.FindDefinitionAsync(owner.TenantId, id));
        }
    }
}
