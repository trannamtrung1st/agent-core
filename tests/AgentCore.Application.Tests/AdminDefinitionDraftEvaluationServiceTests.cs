using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminDefinitionDraftEvaluationServiceTests
{
    [Fact]
    public async Task RunScenarioAsync_records_revision_and_fingerprint()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var evaluation = CreateEvaluationService(admin, resourcesStore, clock);

        var candidate = PublishableExaminerCandidate();
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        _ = await evaluation.UpsertScenarioAsync(
            draft.Revision,
            new DefinitionEvaluationScenarioUpsert(
                draft.DraftId,
                "tool-offered",
                "Knowledge retrieve offered",
                "Synthetic tool-offered check",
                DefinitionEvaluationRequirementLevel.Advisory,
                DefinitionEvaluationCheckType.ToolOffered,
                ToolCatalog.KnowledgeRetrieve,
                clock.GetUtcNow()),
            CancellationToken.None);

        var refreshed = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var result = await evaluation.RunScenarioAsync(refreshed!.DraftId, "tool-offered", CancellationToken.None);
        Assert.Equal(refreshed.Revision, result.DraftRevision);
        Assert.False(string.IsNullOrWhiteSpace(result.ConfigurationFingerprint));
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task Publish_rejects_when_required_eval_evidence_is_stale()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var builtIns = new VersionedBuiltInDefinitions(SampleDefinitions.Examiner);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock);
        var resources = new AgentDefinitionResourceService(admin, resourcesStore, content, clock);
        var validation = new AgentDefinitionDraftValidationService(
            lifecycle,
            resources,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
        var evaluationStore = new InMemoryDefinitionDraftEvaluationStore(admin);
        var evaluation = new AgentDefinitionDraftEvaluationService(
            lifecycle,
            resources,
            evaluationStore,
            ToolConfigurationGates.AllowAll,
            clock);
        var diff = new AgentDefinitionDraftDiffService(lifecycle, resources, builtIns, admin);
        var publish = new AgentDefinitionDraftPublishService(lifecycle, validation, evaluation, diff, ids);

        var candidate = PublishableExaminerCandidate();
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        _ = await evaluation.UpsertScenarioAsync(
            draft.Revision,
            new DefinitionEvaluationScenarioUpsert(
                draft.DraftId,
                "required-tool",
                "Required tool check",
                "prompt",
                DefinitionEvaluationRequirementLevel.Required,
                DefinitionEvaluationCheckType.ToolOffered,
                ToolCatalog.KnowledgeRetrieve,
                clock.GetUtcNow()),
            CancellationToken.None);

        var afterScenario = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var run = await evaluation.RunScenarioAsync(afterScenario!.DraftId, "required-tool", CancellationToken.None);
        Assert.True(run.Passed);

        var updated = await lifecycle.UpdateDraftAsync(
            afterScenario.DraftId,
            afterScenario.Revision,
            candidate with { SystemInstructions = "Bump revision after eval." },
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            publish.PublishDraftAsync(updated.DraftId, updated.Revision, CancellationToken.None).AsTask());
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task UpsertScenarioAsync_rejects_stale_revision_without_changing_scenarios()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var evaluation = CreateEvaluationService(admin, resourcesStore, clock);

        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                PublishableExaminerCandidate(),
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var upsert = new DefinitionEvaluationScenarioUpsert(
            draft.DraftId,
            "tool-offered",
            "Title",
            "Prompt",
            DefinitionEvaluationRequirementLevel.Advisory,
            DefinitionEvaluationCheckType.ToolOffered,
            ToolCatalog.KnowledgeRetrieve,
            clock.GetUtcNow());

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            evaluation.UpsertScenarioAsync(0, upsert, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await evaluation.ListScenariosAsync(draft.DraftId, CancellationToken.None));
    }

    [Fact]
    public async Task UpsertScenarioAsync_does_not_persist_scenario_when_revision_bump_fails()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var inner = new InMemoryAgentDefinitionAdminStore(ids);
        var admin = new FailingBumpAgentDefinitionAdminStore(inner);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(inner, content, ids);
        var evaluation = CreateEvaluationService(
            admin,
            resourcesStore,
            clock,
            new InMemoryDefinitionDraftEvaluationStore(admin));

        var draft = await inner.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                PublishableExaminerCandidate(),
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var upsert = new DefinitionEvaluationScenarioUpsert(
            draft.DraftId,
            "tool-offered",
            "Title",
            "Prompt",
            DefinitionEvaluationRequirementLevel.Advisory,
            DefinitionEvaluationCheckType.ToolOffered,
            ToolCatalog.KnowledgeRetrieve,
            clock.GetUtcNow());

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            evaluation.UpsertScenarioAsync(draft.Revision, upsert, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await evaluation.ListScenariosAsync(draft.DraftId, CancellationToken.None));
        var unchanged = await inner.GetDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.Equal(draft.Revision, unchanged!.Revision);
    }

    [Fact]
    public async Task RemoveScenarioAsync_throws_when_scenario_is_missing()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var evaluation = CreateEvaluationService(
            admin,
            new InMemoryAgentDefinitionResourceAdminStore(admin, new InMemoryDefinitionResourceContentStore(), ids),
            clock);
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                PublishableExaminerCandidate(),
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            evaluation.RemoveScenarioAsync(draft.DraftId, draft.Revision, "missing", CancellationToken.None).AsTask());
        Assert.Equal(404, error.StatusCode);
    }

    private static AgentDefinitionDraftEvaluationService CreateEvaluationService(
        IAgentDefinitionAdminStore admin,
        IAgentDefinitionResourceAdminStore resourcesStore,
        FakeTimeProvider clock,
        IDefinitionDraftEvaluationStore? evaluationStore = null)
    {
        var content = new InMemoryDefinitionResourceContentStore();
        var builtIns = new VersionedBuiltInDefinitions(SampleDefinitions.Examiner);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock);
        var resources = new AgentDefinitionResourceService(admin, resourcesStore, content, clock);
        var store = evaluationStore ?? new InMemoryDefinitionDraftEvaluationStore(admin);
        return new AgentDefinitionDraftEvaluationService(
            lifecycle,
            resources,
            store,
            ToolConfigurationGates.AllowAll,
            clock);
    }

    private sealed class FailingBumpAgentDefinitionAdminStore(IAgentDefinitionAdminStore inner) : IAgentDefinitionAdminStore
    {
        public ValueTask<IReadOnlyList<AgentDefinitionDraftSummary>> ListDraftsAsync(CancellationToken cancellationToken = default) =>
            inner.ListDraftsAsync(cancellationToken);

        public ValueTask<AgentDefinitionDraft?> GetDraftAsync(Guid draftId, CancellationToken cancellationToken = default) =>
            inner.GetDraftAsync(draftId, cancellationToken);

        public ValueTask<AgentDefinitionDraft> CreateDraftAsync(
            AgentDefinitionDraftCreate create,
            CancellationToken cancellationToken = default) =>
            inner.CreateDraftAsync(create, cancellationToken);

        public ValueTask<AgentDefinitionDraft> UpdateDraftAsync(
            AgentDefinitionDraftUpdate update,
            CancellationToken cancellationToken = default) =>
            inner.UpdateDraftAsync(update, cancellationToken);

        public ValueTask<AgentDefinitionDraft> BumpDraftRevisionAsync(
            AgentDefinitionDraftRevisionBump bump,
            CancellationToken cancellationToken = default) =>
            throw AgentCoreErrors.Conflict("Draft revision is stale.");

        public ValueTask<AgentDefinitionPublication> PublishDraftAsync(
            AgentDefinitionDraftPublish publish,
            CancellationToken cancellationToken = default) =>
            inner.PublishDraftAsync(publish, cancellationToken);

        public ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListPublicationsAsync(
            string? definitionId = null,
            CancellationToken cancellationToken = default) =>
            inner.ListPublicationsAsync(definitionId, cancellationToken);

        public ValueTask<AgentDefinitionPublication?> GetPublicationAsync(
            string definitionId,
            int version,
            CancellationToken cancellationToken = default) =>
            inner.GetPublicationAsync(definitionId, version, cancellationToken);

        public ValueTask<AgentDefinitionPublication> DeprecatePublicationAsync(
            AgentDefinitionPublicationDeprecate deprecate,
            CancellationToken cancellationToken = default) =>
            inner.DeprecatePublicationAsync(deprecate, cancellationToken);
    }

    private static AgentDefinitionCandidate PublishableExaminerCandidate() =>
        AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            Environment = RoleEnvironment.Empty with
            {
                ToolAllowlist = [ToolCatalog.KnowledgeRetrieve]
            }
        };

    private sealed class VersionedBuiltInDefinitions(AgentDefinition definition) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
