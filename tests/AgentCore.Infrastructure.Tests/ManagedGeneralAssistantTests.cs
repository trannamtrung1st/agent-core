using AgentCore.Application.Agents;
using AgentCore.Application.Admin;
using AgentCore.Infrastructure.Workspaces;
using AgentCore.Domain.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed class ManagedGeneralAssistantTests
{
    private sealed class ReleaseCatalog(IBuiltInAgentDefinitionStore source, int version) : IBuiltInAgentDefinitionStore
    {
        public int Version { get; set; } = version;
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => source.ListAsync(cancellationToken);
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) => source.GetAsync(id,
            version ?? (id == ManagedGeneralAssistant.DefinitionId ? Version : null), cancellationToken);
    }
    private static TenantAgentConfigurationService Configuration(Fixture f, IBuiltInAgentDefinitionStore source, ITenantModelCatalogReader? models = null)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "agents"))) dir = dir.Parent;
        var resources = new InheritedDefinitionResourceCatalog(f.Resources, source, new FileApprovedKnowledgeCatalog(Path.Combine(dir!.FullName, "agents")), f.RunContent);
        return new(f.Iam, (ITenantAgentStore)f.Iam, (ITenantDefinitionStore)f.Iam, source, resources, f.Authorizer,
            models ?? new SyntheticTenantModelCatalogReader(f.Iam), AgentCore.Application.Tools.ToolConfigurationGates.AllowAll, new(), f.Ids, f.Clock,
            aliases: SyntheticProviderAliases.Default, identity: f.Identity, content: f.RunContent);
    }
    private static ManagedGeneralAssistantService Coordinator(Fixture f, IBuiltInAgentDefinitionStore source) =>
        new(f.Iam, (ITenantAgentStore)f.Iam, source, f.Ids, f.Clock, () => Configuration(f, source));
    private static async Task<(BootstrapIdentityResult Owner, TenantSessionInitiator Human)> Bootstrap(Fixture f)
    {
        var owner = await f.Identity.BootstrapAsync("assistant-owner@example.test", "Owner", "explicit-strong-assistant-password", "Assistant tenant", "assistant-tenant");
        var login = await f.Identity.SignInAsync("assistant-owner@example.test", "explicit-strong-assistant-password");
        return (owner, new(owner.AccountId, login!.Session.SessionId, owner.OwnerPrincipalId));
    }
    private static async Task<long> Revision(Fixture f, Guid tenant) => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Provisioning_is_concurrent_idempotent_scoped_and_survives_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var (owner, human) = await Bootstrap(f); var service = Coordinator(f, f.BuiltIns); var tenant = owner.TenantId;
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.ReconcileAsync(tenant).AsTask()));
        var agent = results[0]; Assert.All(results, item => Assert.Equal(agent.Instance.InstanceId, item.Instance.InstanceId));
        Assert.Equal(ManagedGeneralAssistant.Kind, agent.ManagedKind); Assert.True(agent.Instance.ShowInChat); Assert.False(agent.Instance.PersonaCustomized);
        Assert.Equal("General Assistant", agent.Instance.Persona.Name);
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        Assert.Single(state.SystemIdentities, i => i.ManagedKind is not null);
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == ManagedGeneralAssistant.ProvisionAction && a.ActorPrincipalId == Guid.Empty);
        var space = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!;
        Assert.True((await f.Authorizer.CheckAsync(agent.PrincipalId, "workspaces.write", TenantWorkspaceRules.Home(tenant, space.SpaceId))).Allowed);
        foreach (var permission in new[] { "agents.update", "credentials.use", "browser.use", "sandbox.manage", "tasks.delegate", "approvals.approve", "tenant.governance.manage" })
            Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, permission, AccessScope.ForTenant(tenant))).Allowed);
        var list = await f.ChatService().ListAgentsAsync(tenant, human, pageSize: 1);
        Assert.Equal(agent.Instance.InstanceId, list.Items.Single().InstanceId); Assert.True(list.Items.Single().SystemManaged);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns);
            var durable = (await reopened.FindTenantAgentAsync(tenant, agent.Instance.InstanceId))!;
            Assert.Equal(agent.ManagedKind, durable.ManagedKind); Assert.Equal(space.SpaceId, (await reopened.FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!.SpaceId);
            Assert.True(durable.Instance.ShowInChat);
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var ordinary = await f.Agents.CreateAsync(human.PrincipalId, tenant, ManagedGeneralAssistant.DefinitionId, 36, await Revision(f, tenant));
        Assert.Null(ordinary.ManagedKind);
        var otherSpace = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, ordinary.Instance.InstanceId))!;
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "spaces.read", AccessScope.ForResource(tenant, "space", otherSpace.SpaceId.ToString("D")))).Allowed);
        Assert.False((await f.Authorizer.CheckAsync(agent.PrincipalId, "workspaces.read", TenantWorkspaceRules.Home(tenant, otherSpace.SpaceId))).Allowed);
        if (sqlite)
        {
            await using var unique = f.Contexts!.CreateDbContext();
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => unique.Database.ExecuteSqlInterpolatedAsync($"UPDATE SystemIdentities SET ManagedKind={ManagedGeneralAssistant.Kind} WHERE AgentInstanceId={ordinary.Instance.InstanceId.ToString("D")}"));
        }
        await f.Agents.SetLifecycleAsync(human.PrincipalId, tenant, ordinary.Instance.InstanceId, AgentInstanceLifecycle.Archived, await Revision(f, tenant), ordinary.Instance.Revision);
        var currentRevision = await Revision(f, tenant);
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.Agents.SetLifecycleAsync(human.PrincipalId, tenant, agent.Instance.InstanceId,
            AgentInstanceLifecycle.Archived, currentRevision, agent.Instance.Revision).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Visibility_reset_and_canonical_instructions_preserve_identity_data_and_IAM(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var tenant = owner.TenantId; var agent = await Coordinator(f, f.BuiltIns).ReconcileAsync(tenant); var config = Configuration(f, f.BuiltIns);
        agent = await config.SetChatVisibilityAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, false, await Revision(f, tenant), agent.Instance.Revision, initiator: human);
        var hidden = await f.ChatService().ListAgentsAsync(tenant, human, query: "General Assistant"); Assert.Empty(hidden.Items); Assert.Equal(0, hidden.Total);
        Assert.Single((await ((ITenantAgentStore)f.Iam).ListTenantAgentsAsync(tenant, null, 0, 30)).Items);
        var supplement = System.Text.Json.JsonSerializer.SerializeToElement("Answer with care.");
        agent = await config.PatchSettingsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, "instructions", await Revision(f, tenant), agent.Instance.Revision,
            new Dictionary<string, System.Text.Json.JsonElement> { ["agentInstructions"] = supplement }, [], initiator: human);
        var baseline = (await f.BuiltIns.GetAsync(ManagedGeneralAssistant.DefinitionId))!;
        var effective = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        Assert.StartsWith(baseline.AgentInstructions, effective.AgentInstructions); Assert.Contains("Answer with care.", effective.AgentInstructions);
        agent = await config.UpdatePersonaAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, baseline.Identity with { Name = "My assistant" },
            await Revision(f, tenant), agent.Instance.Revision, agent.Instance.PersonaRevision, initiator: human);
        Assert.True(agent.Instance.PersonaCustomized);
        var localSkill = await config.WriteSkillAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, "create", await Revision(f, tenant), agent.Instance.Revision,
            input: new InstanceSkillInput("Local procedure", "Retained local work", "Use the user's explicit request.", SkillProjection.OnDemand, true, ["chat.respond"]), initiator: human);
        var localResource = await config.UpsertResourceAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, await Revision(f, tenant), localSkill.InstanceRevision,
            new(null, null, "local/retained.txt", AgentDefinitionResourceKind.Reference, "text/plain", System.Text.Encoding.UTF8.GetBytes("RETAINED-LOCAL-RESOURCE")), initiator: human);
        agent = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, agent.Instance.InstanceId))!;
        var before = (await f.Iam.ReadTenantAsync(tenant))!; var privateSpace = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!;
        var home = AgentWorkspacePhysicalPaths.SpaceHomeDirectory(Path.Combine(f.Root, "workspaces"), tenant, privateSpace.SpaceId);
        Directory.CreateDirectory(home); var retainedFile = Path.Combine(home, "retained.txt"); await File.WriteAllTextAsync(retainedFile, "RETAINED-FILE");
        var operation = f.Ids.NewId(); var expected = agent.Instance.Revision;
        var reset = await config.ResetDefaultsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, operation, before.Tenant.Revision, expected, initiator: human);
        Assert.Equal("applied", reset.Status);
        var again = await config.ResetDefaultsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, operation, before.Tenant.Revision, expected, initiator: human);
        Assert.Equal("already_applied", again.Status);
        var saved = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, agent.Instance.InstanceId))!;
        Assert.False(saved.Instance.ShowInChat); Assert.False(saved.Instance.PersonaCustomized); Assert.Null(saved.Instance.SettingsOverrides);
        Assert.Equal("RETAINED-FILE", await File.ReadAllTextAsync(retainedFile));
        Assert.Contains((await config.ReadSkillsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId)).Items, item => item.Key == localSkill.Skill.Key);
        Assert.Equal("RETAINED-LOCAL-RESOURCE", System.Text.Encoding.UTF8.GetString((await config.ReadResourceContentAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, localResource.Resource!.Key)).Bytes));
        Assert.Equal(baseline.Identity, saved.Instance.Persona); Assert.Equal(agent.PrincipalId, saved.PrincipalId);
        Assert.Equal(before.Assignments, (await f.Iam.ReadTenantAsync(tenant))!.Assignments);
        Assert.Equal(privateSpace, await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId));
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == ManagedGeneralAssistant.ResetAction);
        await Assert.ThrowsAsync<SecurityConflictException>(() => config.ResetDefaultsAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, f.Ids.NewId(), before.Tenant.Revision, expected, initiator: human).AsTask());
        if (sqlite)
        {
            var reopen = new SqliteIamStore(f.Contexts!, builtIns: f.BuiltIns);
            var durable = (await reopen.FindTenantAgentAsync(tenant, agent.Instance.InstanceId))!;
            Assert.False(durable.Instance.ShowInChat); Assert.False(durable.Instance.PersonaCustomized);
        }
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Automatic_adoption_follows_persona_provenance_and_preserves_visibility_and_overrides(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var source = new ReleaseCatalog(f.BuiltIns, 35); var service = Coordinator(f, source); var tenant = owner.TenantId;
        var agent = await service.ReconcileAsync(tenant); Assert.Equal(35, agent.Instance.ActiveVersion);
        var config = Configuration(f, source);
        agent = await config.SetChatVisibilityAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, false, await Revision(f, tenant), agent.Instance.Revision, initiator: human);
        source.Version = 36; var updated = await service.ReconcileAsync(tenant);
        Assert.Equal(36, updated.Instance.ActiveVersion); Assert.False(updated.Instance.ShowInChat); Assert.Equal("General Assistant", updated.Instance.Persona.Name);
        Assert.Equal(agent.PrincipalId, updated.PrincipalId);
        var currentRevision = await Revision(f, tenant);
        await Assert.ThrowsAsync<SecurityConflictException>(() => config.AdoptVersionAsync(human.PrincipalId, tenant, updated.Instance.InstanceId, 35,
            currentRevision, updated.Instance.Revision, initiator: human).AsTask());
        Assert.Single(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == ManagedGeneralAssistant.AdoptAction);
        source.Version = 35;
        Assert.Equal("assistant_release_unavailable", (await Assert.ThrowsAsync<SecurityConflictException>(() => service.ReconcileAsync(tenant).AsTask())).ReasonCode);
        Assert.Equal("assistant_release_unavailable", (await Assert.ThrowsAsync<SecurityConflictException>(() => config.ResetDefaultsAsync(human.PrincipalId,
            tenant, updated.Instance.InstanceId, f.Ids.NewId(), currentRevision, updated.Instance.Revision, initiator: human).AsTask())).ReasonCode);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Saved_hidden_conversation_keeps_its_admitted_pin_and_uses_the_release_on_its_next_Run(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var source = new ReleaseCatalog(f.BuiltIns, 35); var coordinator = Coordinator(f, source); var tenant = owner.TenantId;
        var agent = await coordinator.ReconcileAsync(tenant); var config = Configuration(f, source);
        agent = await config.UpdatePersonaAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, agent.Instance.Persona with { Name = "Personal assistant" },
            await Revision(f, tenant), agent.Instance.Revision, agent.Instance.PersonaRevision, initiator: human);
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
            (ITenantDefinitionStore)f.Iam, source, f.Identity, f.Authorizer, new SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, f.Clock);
        var runs = new TenantRunService(f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, f.RunConfiguration(source), f.Identity,
            f.Authorizer, new SyntheticTenantModelCatalogReader(f.Iam), f.Ids, f.Clock);
        var session = await sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(f, tenant), agent.Instance.Revision);
        var first = await runs.SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "First accepted request", await Revision(f, tenant), session.Revision);
        var firstPin = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, first.RunId))!;
        Assert.Equal(35, firstPin.DefinitionVersion);
        agent = await config.SetChatVisibilityAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, false, await Revision(f, tenant), agent.Instance.Revision, initiator: human);
        source.Version = 36;
        Assert.Equal(35, (await f.RunConfiguration(source).CurrentResourcesAsync(tenant, agent.Instance.InstanceId)).Definition.Version);
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.RunConfiguration(source).ResolveInSpaceAsync(tenant, agent.Instance.InstanceId, session.ContextSelection!, CancellationToken.None).AsTask());
        var adopted = await coordinator.ReconcileAsync(tenant); Assert.Equal("Personal assistant", adopted.Instance.Persona.Name); Assert.False(adopted.Instance.ShowInChat);
        Assert.Equal(35, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, first.RunId))!.DefinitionVersion);
        await runs.CancelAsync(tenant, first.RunId, human, await Revision(f, tenant), firstPin.Revision);
        var current = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!;
        var second = await runs.SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Next accepted request", await Revision(f, tenant), current.Snapshot.Revision);
        var secondPin = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, second.RunId))!;
        Assert.Equal(36, secondPin.DefinitionVersion); Assert.Equal("Personal assistant", secondPin.PinnedPersona.Name);
        Assert.Equal(2, (await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(tenant, session.Owner.SessionId)).Count);
        Assert.Empty((await f.ChatService().ListAgentsAsync(tenant, human)).Items);
    }

    private sealed class EmptyModels : IModelCatalog
    {
        public string DefaultOfferingId => "";
        public string? DefaultReasoningEffort => null;
        public IReadOnlyList<ModelDescriptor> Models => [];
        public ModelDescriptor Default => throw new AgentCore.Application.Sessions.AgentCoreException("ModelUnavailable", "No model configured.", 409);
        public ModelDescriptor? Get(string key) => null;
    }
    private sealed class ChangingModels(ITenantModelCatalogReader source) : ITenantModelCatalogReader
    {
        public bool Missing { get; set; }
        public ValueTask<IModelCatalog> ReadAsync(TenantSecurityState state, CancellationToken ct = default) =>
            Missing ? ValueTask.FromResult<IModelCatalog>(new EmptyModels()) : source.ReadAsync(state, ct);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Incompatible_model_release_leaves_overrides_intact_blocks_new_work_and_recovers_on_retry(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var source = new ReleaseCatalog(f.BuiltIns, 35); var models = new ChangingModels(new SyntheticTenantModelCatalogReader(f.Iam));
        var config = Configuration(f, source, models);
        var coordinator = new ManagedGeneralAssistantService(f.Iam, (ITenantAgentStore)f.Iam, source, f.Ids, f.Clock, () => config);
        var agent = await coordinator.ReconcileAsync(owner.TenantId);
        var catalog = await models.ReadAsync((await f.Iam.ReadTenantAsync(owner.TenantId))!);
        var preference = System.Text.Json.JsonSerializer.SerializeToElement(new { mode = "fixed", offeringId = catalog.DefaultOfferingId });
        agent = await config.PatchSettingsAsync(human.PrincipalId, owner.TenantId, agent.Instance.InstanceId, "modelDefaults", await Revision(f, owner.TenantId),
            agent.Instance.Revision, new Dictionary<string, System.Text.Json.JsonElement> { ["preference"] = preference }, [], initiator: human);
        source.Version = 36; models.Missing = true;
        Assert.Equal("assistant_upgrade_model_incompatible", (await Assert.ThrowsAsync<SecurityConflictException>(() => coordinator.ReconcileAsync(owner.TenantId).AsTask())).ReasonCode);
        var unchanged = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, agent.Instance.InstanceId))!;
        Assert.Equal(agent.Instance.Revision, unchanged.Instance.Revision); Assert.Equal(35, unchanged.Instance.ActiveVersion);
        Assert.Equal(agent.Instance.SettingsOverrides!.ModelPreference, unchanged.Instance.SettingsOverrides!.ModelPreference);
        var privateSpace = (await ((ITenantSpaceStore)f.Iam).FindPrivateSpaceAsync(owner.TenantId, agent.Instance.InstanceId))!;
        await Assert.ThrowsAsync<SecurityConflictException>(() => f.RunConfiguration(source).ResolveInSpaceAsync(owner.TenantId, agent.Instance.InstanceId,
            new(owner.TenantId, privateSpace.SpaceId, null, 0, 1, f.Clock.GetUtcNow()), CancellationToken.None).AsTask());
        models.Missing = false; var recovered = await coordinator.ReconcileAsync(owner.TenantId);
        Assert.Equal(36, recovered.Instance.ActiveVersion); Assert.Equal(agent.Instance.SettingsOverrides.ModelPreference, recovered.Instance.SettingsOverrides!.ModelPreference);
    }

    [Fact]
    public async Task Additive_migration_roundtrip_preserves_populated_ordinary_identity_and_retirement_guards()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var ordinary = await f.Agents.CreateAsync(human.PrincipalId, owner.TenantId, "general-assistant", 35, await Revision(f, owner.TenantId));
        await using (var db = f.Contexts!.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261027060000_TaskDelegation");
            await db.Database.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        }
        var saved = (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(owner.TenantId, ordinary.Instance.InstanceId))!;
        Assert.Null(saved.ManagedKind); Assert.True(saved.Instance.ShowInChat); Assert.False(saved.Instance.PersonaCustomized);
        Assert.Equal(ordinary.PrincipalId, saved.PrincipalId); Assert.Equal(ordinary.Instance.Persona, saved.Instance.Persona);
        var managed = await Coordinator(f, f.BuiltIns).ReconcileAsync(owner.TenantId);
        Assert.NotEqual(saved.Instance.InstanceId, managed.Instance.InstanceId);
        Assert.Equal(2, (await ((ITenantAgentStore)f.Iam).ListTenantAgentsAsync(owner.TenantId, null, 0, 30)).Total);
    }

    private sealed class NoModels : ITenantModelCatalogReader
    {
        public ValueTask<IModelCatalog> ReadAsync(TenantSecurityState state, CancellationToken ct = default) => ValueTask.FromResult<IModelCatalog>(new EmptyModels());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Missing_model_setup_is_truthful_and_does_not_create_a_conversation_or_substitute_a_route(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true); var (owner, human) = await Bootstrap(f);
        var agent = await Coordinator(f, f.BuiltIns).ReconcileAsync(owner.TenantId); var models = new NoModels();
        var chat = new TenantChatService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantDefinitionStore)f.Iam, f.BuiltIns, f.Identity, f.Authorizer, models);
        var candidates = await chat.ListAgentsAsync(owner.TenantId, human);
        Assert.Equal("no_model_configured", candidates.Items.Single().SetupState);
        Assert.Empty((await chat.ModelsAsync(owner.TenantId, human, agent.Instance.InstanceId)).Models);
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam,
            (ITenantDefinitionStore)f.Iam, f.BuiltIns, f.Identity, f.Authorizer, models, new(), f.Ids, f.Clock);
        var revision = await Revision(f, owner.TenantId);
        var failure = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, human,
            SessionMode.Text, revision, agent.Instance.Revision).AsTask());
        Assert.Equal("ModelUnavailable", failure.Code); Assert.Equal(revision, await Revision(f, owner.TenantId));
    }

}
