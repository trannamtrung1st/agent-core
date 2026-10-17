using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_instance_configuration_resolves_owned_resources_and_adopts_exact_versions_across_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        async ValueTask<long> Revision() => (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var draft = await f.Service.ForkTemplateAsync(owner.OwnerPrincipalId, owner.TenantId, "customer-support", 3, "Owned support", await Revision());
        var definitionId = Guid.Parse(draft.DefinitionId);
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, await Revision(), draft.Revision);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision());
        var id = agent.Instance.InstanceId;
        var initial = await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.NotEmpty(initial.Resources); Assert.All(initial.Resources, r => Assert.StartsWith("definition:", r.Key));
        Assert.Empty(await f.Resources.ListPublicationResourcesAsync(draft.DefinitionId, 1));
        Assert.Equal(publication.Payload.AgentInstructions, initial.Definition.AgentInstructions);
        var persona = agent.Instance.Persona with { Name = "Independent support persona" };
        var changed = await configuration.UpdatePersonaAsync(owner.OwnerPrincipalId, owner.TenantId, id, persona, await Revision(), 1, 1);
        var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, definitionId, 1, await Revision());
        var edited = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId,
            fork.Candidate with { AgentInstructions = "New explicit owned publication." }, await Revision(), fork.Revision);
        await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, fork.DraftId, await Revision(), edited.Revision);
        Assert.Equal(publication.Payload.AgentInstructions, (await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Definition.AgentInstructions);
        var before = await Revision();
        var adopted = await configuration.AdoptVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 2, before, changed.Instance.Revision);
        Assert.Equal(2, adopted.Instance.ActiveVersion); Assert.Equal(persona, adopted.Instance.Persona);
        Assert.Equal(before + 1, await Revision());
        Assert.Equal(3, adopted.Instance.Revision); Assert.Equal(2, adopted.Instance.PersonaRevision);
        var after = await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id);
        Assert.Equal("New explicit owned publication.", after.Definition.AgentInstructions); Assert.Equal(initial.Resources.Count, after.Resources.Count);
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await f.Access.AssignRoleAsync(owner.OwnerPrincipalId, owner.TenantId, agent.PrincipalId, state.Roles.Single(r => r.Key == "contributor").RoleId,
            AccessScope.ForResource(owner.TenantId, "agent", id.ToString("D")), state.Tenant.Revision);
        Assert.Equal(id, (await configuration.ReadAsync(agent.PrincipalId, owner.TenantId, id)).Agent.Instance.InstanceId);
        var scopedRevision = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.AdoptVersionAsync(agent.PrincipalId, owner.TenantId, id, 1, scopedRevision, 3).AsTask());
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            var persisted = await ConfigurationServices(f, reopened).Configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id);
            Assert.Equal(after.Definition.AgentInstructions, persisted.Definition.AgentInstructions);
            Assert.Equal(persona, persisted.Agent.Instance.Persona); Assert.Equal(2, persisted.Definition.Version);
            Assert.Equal(after.Resources.Select(r => r.Key), persisted.Resources.Select(r => r.Key));
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.UpdateWithExpectedRevisionAsync(new(id, 3, ActiveVersion: 1), DateTimeOffset.UtcNow).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => f.Instances.UpdateActiveVersionAsync(id, 99, DateTimeOffset.UtcNow).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_persona_cas_checks_current_account_security_and_instance_witnesses_without_partial_audit(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "examiner", 1, 1);
        var id = agent.Instance.InstanceId; var store = (ITenantAgentStore)f.Iam;
        var before = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var now = DateTimeOffset.UtcNow; var persona = agent.Instance.Persona with { Name = "CAS winner" };
        AuthorizationAudit Audit(Guid actor) => new(f.Ids.NewId(), owner.TenantId, actor, "agents.persona.update", id.ToString("D"), "allowed", "test", now);
        var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        var bad = new TenantAgentConfigurationChange(id, 1, persona, 99);
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, bad, now, Audit(owner.OwnerPrincipalId)).AsTask());
        Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision,
                new(id, 1, persona, 1), now, Audit(owner.OwnerPrincipalId), cancellation.Token).AsTask());
        }
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, new(id, 1, persona, 1), now, Audit(owner.OwnerPrincipalId)).AsTask()));
        Assert.Single(attempts, x => x is not null);
        Assert.Equal(count + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        Assert.Equal(2, (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
        var account = await f.Identity.RegisterAsync("operator@example.test", "Operator", "strong-operator-password");
        before = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invite = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, before.Roles.Single(r => r.Key == "contributor").RoleId, before.Tenant.Revision);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invite.Invitation.InvitationId, invite.Token);
        before = (await f.Iam.ReadTenantAsync(owner.TenantId))!; next = before with { Tenant = before.Tenant with { Revision = before.Tenant.Revision + 1 } };
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        var failure = await Assert.ThrowsAsync<AccessDeniedException>(() => store.TryChangeTenantAgentConfigurationAsync(next, before.Tenant.Revision, new(id, 2, persona with { Name = "Must not save" }, 2), now, Audit(user.PrincipalId)).AsTask());
        Assert.Equal("account_unavailable", failure.ReasonCode);
        Assert.Equal(before.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(persona, (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Persona);
        await Assert.ThrowsAsync<AccessDeniedException>(() => configuration.UpdatePersonaAsync(user.PrincipalId, owner.TenantId, id, persona, before.Tenant.Revision, 2, 2).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tenant_adoption_rechecks_publication_witness_and_atomic_automation_plan(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var (agents, configuration) = ConfigurationServices(f);
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Adoption", 1); var definition = Guid.Parse(draft.DefinitionId);
        var p1 = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1);
        var agent = await agents.CreateFromPublicationAsync(owner.OwnerPrincipalId, owner.TenantId, definition, 1, 3);
        var id = agent.Instance.InstanceId; var store = (ITenantAgentStore)f.Iam;
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }; var now = DateTimeOffset.UtcNow;
        var audit = new AuthorizationAudit(f.Ids.NewId(), owner.TenantId, owner.OwnerPrincipalId, "agents.version.adopt", id.ToString("D"), "allowed", "test", now);
        var invalid = new TenantAgentConfigurationChange(id, 1, ActiveVersion: 1, ExpectedPublicationRevision: 99, DefinitionSkills: [], AutomationPolicyChanges: []);
        var denied = await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, invalid, now, audit).AsTask());
        Assert.Equal("resource_changed", denied.ReasonCode);
        var stalePlan = invalid with { ExpectedPublicationRevision = 1, AutomationPolicyChanges = [new(Guid.NewGuid(), 1, AgentCore.Domain.Triggers.AutomationStatus.Active, null, null)] };
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, stalePlan, now, audit).AsTask());
        Assert.Equal(1, (await store.FindTenantAgentAsync(owner.TenantId, id))!.Instance.Revision);
        Assert.Equal(state.Tenant.Revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        ITriggerStore triggers = f.Contexts is not null ? new SqliteTriggerStore(f.Contexts) : ((InMemoryAgentInstanceStore)f.Instances).TriggerStore!;
        var triggerOwner = new TriggerOwner(id, Guid.NewGuid());
        await f.SessionStore.SaveProfileAsync(new UserProfile(triggerOwner.ProfileId, 1, new Dictionary<string, UserProfileValue>(), now), 0);
        var automation = await triggers.CreateAsync(new Automation(Guid.NewGuid(), triggerOwner, AutomationStatus.Active, "Review inventory",
            new FixedIntervalSchedule(60, now), now.AddMinutes(1), null, 0, 1, 1,
            new TriggerProvenance(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null));
        var reconciled = await configuration.AdoptVersionAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, state.Tenant.Revision, 1);
        Assert.Equal(2, reconciled.Instance.Revision);
        Assert.Equal(AutomationStatus.SuspendedPolicy, (await triggers.GetAsync(triggerOwner, automation.AutomationId))!.Status);
        Assert.Equal(2, (await triggers.GetAsync(triggerOwner, automation.AutomationId))!.Revision);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        await f.Service.DeprecateAsync(owner.OwnerPrincipalId, owner.TenantId, definition, 1, state.Tenant.Revision, 1);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!; next = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TryChangeTenantAgentConfigurationAsync(next, state.Tenant.Revision, invalid with { ExpectedPublicationRevision = 2, ExpectedInstanceRevision = 2 }, now, audit).AsTask());
        Assert.Equal(p1.Payload.AgentInstructions, (await configuration.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id)).Definition.AgentInstructions);
    }

    private static (TenantAgentService Agents, TenantAgentConfigurationService Configuration) ConfigurationServices(Fixture f, IIamStore? reopened = null, IDefinitionResourceContentStore? content = null,
        Func<IBuiltInAgentDefinitionStore, IBuiltInAgentDefinitionStore>? wrapBuiltIns = null)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "agents"))) directory = directory.Parent;
        var root = Path.Combine(directory!.FullName, "agents"); IBuiltInAgentDefinitionStore builtIns = new FileAgentDefinitionStore(root, SyntheticProviderAliases.Default);
        if (wrapBuiltIns is not null) builtIns = wrapBuiltIns(builtIns);
        var store = reopened ?? f.Iam; var authorizer = new IamAuthorizer(store);
        ITriggerStore triggers;
        if (f.Contexts is not null) triggers = new SqliteTriggerStore(f.Contexts);
        else triggers = ((InMemoryAgentInstanceStore)f.Instances).TriggerStore ??= new InMemoryTriggerStore();
        var resources = new InheritedDefinitionResourceCatalog(f.Resources, builtIns, new FileApprovedKnowledgeCatalog(root), f.Content);
        return (new(store, (ITenantAgentStore)store, builtIns, authorizer, f.Ids, TimeProvider.System, ownedDefinitions: (ITenantDefinitionStore)store),
            new(store, (ITenantAgentStore)store, (ITenantDefinitionStore)store, builtIns, resources, authorizer,
                ModelCatalogFactory.Synthetic(), ToolConfigurationGates.AllowAll, new(triggers, f.SessionStore), f.Ids, TimeProvider.System, aliases: SyntheticProviderAliases.Default, content: content ?? f.Content, identity: f.Identity));
    }
}
