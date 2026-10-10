using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminDefinitionDraftDiffServiceTests
{
    [Theory]
    [InlineData("coreEvent", "Built-in Events: Allowed", "Webhook Events: Not allowed")]
    [InlineData("applicationEvent", "Webhook Events: Allowed", "Built-in Events: Not allowed")]
    public async Task Trigger_diff_names_independent_event_permissions(string kind, string allowed, string denied)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = CreateAdminStore(ids);
        var resources = new InMemoryAgentDefinitionResourceAdminStore(admin, new InMemoryDefinitionResourceContentStore(), ids);
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        { TriggerPolicy = new(false, false, false, false, false, false, 1, 1, 1, [kind]) };
        var draft = await admin.CreateDraftAsync(new("examiner", candidate, DefinitionDraftSourceKind.New, null, clock.GetUtcNow()), default);
        var result = await CreateDiffService(admin, resources, clock).GetDraftDiffAsync(draft.DraftId, default);
        var section = Assert.Single(result.Sections, s => s.SectionId == "triggerPolicy");
        Assert.Equal("Automation trigger permissions", section.Label);
        Assert.Contains(allowed, section.AfterSummary);
        Assert.Contains(denied, section.AfterSummary);
        Assert.Contains("Schedule: Not allowed", section.AfterSummary);
        Assert.Contains("Disabled (configured permissions inactive)", section.AfterSummary);
    }

    [Theory]
    [InlineData("coreEvent", "Webhook Events")]
    [InlineData("applicationEvent", "Built-in Events")]
    public async Task Trigger_diff_exposes_exact_subtype_expansion_against_restricted_baseline(string kind, string expanded)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-10T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = CreateAdminStore(ids);
        var definition = SampleDefinitions.Examiner with
        { TriggerPolicy = new(true, false, false, false, false, false, 1, 1, 1, [kind]) };
        var resources = new InMemoryAgentDefinitionResourceAdminStore(admin, new InMemoryDefinitionResourceContentStore(), ids);
        var lifecycle = new AgentDefinitionLifecycleService(new VersionedBuiltInDefinitions(definition), admin,
            SyntheticProviderAliases.Default, clock, ids);
        var draft = await lifecycle.ForkDraftAsync("examiner", 1, DefinitionDraftSourceKind.ForkBuiltIn, default);
        draft = await lifecycle.UpdateDraftAsync(draft.DraftId, draft.Revision, draft.Candidate with
        { TriggerPolicy = definition.TriggerPolicy with { AllowedSourceKinds = ["coreEvent", "applicationEvent"] } }, default);
        var diff = await CreateDiffService(admin, resources, clock, definition).GetDraftDiffAsync(draft.DraftId, default);
        var section = Assert.Single(diff.Sections, s => s.SectionId == "triggerPolicy");
        Assert.Equal(DefinitionDiffChangeKind.Modified, section.ChangeKind);
        Assert.Contains($"{expanded}: Not allowed", section.BeforeSummary);
        Assert.Contains($"{expanded}: Allowed", section.AfterSummary);
        Assert.Contains("Schedule: Not allowed", section.BeforeSummary);
        Assert.Contains("Schedule: Not allowed", section.AfterSummary);
    }

    [Fact]
    public async Task GetDraftDiffAsync_rejects_when_draft_revision_changes_during_resource_resolution()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var innerResources = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var resourcesStore = new BumpDraftRevisionOnListResourceStore(innerResources, admin, clock);
        var diff = CreateDiffService(admin, resourcesStore, clock);

        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner);
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            diff.GetDraftDiffAsync(draft.DraftId, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task GetDraftDiffAsync_new_draft_lists_candidate_sections_as_added()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var diff = CreateDiffService(admin, resourcesStore, clock);

        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            SystemInstructions = "P7F new-draft diff sentinel."
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var result = await diff.GetDraftDiffAsync(draft.DraftId, CancellationToken.None);
        Assert.Equal(draft.Revision, result.DraftRevision);
        var instructions = Assert.Single(result.Sections, section => section.SectionId == "instructions");
        Assert.Equal(DefinitionDiffChangeKind.Added, instructions.ChangeKind);
        Assert.Contains("P7F new-draft diff sentinel", instructions.AfterSummary, StringComparison.Ordinal);
        Assert.Null(instructions.BeforeSummary);
    }

    [Fact]
    public async Task GetDraftDiffAsync_marks_initiative_trigger_list_change_against_fork_baseline()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = CreateAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var diff = CreateDiffService(admin, resourcesStore, clock);

        var builtIns = new VersionedBuiltInDefinitions(SampleDefinitions.Examiner);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock,
            new SystemIdGenerator(clock));
        var forked = await lifecycle.ForkDraftAsync(
            "examiner",
            1,
            DefinitionDraftSourceKind.ForkBuiltIn,
            CancellationToken.None);
        var candidate = forked.Candidate with
        {
            InitiativePolicy = forked.Candidate.InitiativePolicy with
            {
                Triggers = ["longSilence", "environmentUpdate"]
            }
        };
        var updated = await lifecycle.UpdateDraftAsync(
            forked.DraftId,
            forked.Revision,
            candidate,
            CancellationToken.None);

        var result = await diff.GetDraftDiffAsync(updated.DraftId, CancellationToken.None);
        var initiative = Assert.Single(result.Sections, section => section.SectionId == "initiative");
        Assert.Equal(DefinitionDiffChangeKind.Modified, initiative.ChangeKind);
        Assert.Contains("environmentUpdate", initiative.AfterSummary, StringComparison.Ordinal);
    }

    private static InMemoryAgentDefinitionAdminStore CreateAdminStore(SystemIdGenerator ids)
    {
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        admin.EventStore = new InMemoryAdminEventStore(ids);
        return admin;
    }

    private static AgentDefinitionDraftDiffService CreateDiffService(
        IAgentDefinitionAdminStore admin,
        IAgentDefinitionResourceAdminStore resourcesStore,
        FakeTimeProvider clock,
        AgentDefinition? definition = null)
    {
        var content = new InMemoryDefinitionResourceContentStore();
        var builtIns = new VersionedBuiltInDefinitions(definition ?? SampleDefinitions.Examiner);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock,
            new SystemIdGenerator(clock));
        var resources = new AgentDefinitionResourceService(admin, resourcesStore, content, clock, lifecycle);
        return new AgentDefinitionDraftDiffService(lifecycle, resources, builtIns, admin);
    }

    private sealed class VersionedBuiltInDefinitions(AgentDefinition definition) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }

    private sealed class BumpDraftRevisionOnListResourceStore(
        IAgentDefinitionResourceAdminStore inner,
        IAgentDefinitionAdminStore drafts,
        TimeProvider time) : IAgentDefinitionResourceAdminStore
    {
        public async ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
            Guid draftId,
            CancellationToken cancellationToken = default)
        {
            var list = await inner.ListDraftResourcesAsync(draftId, cancellationToken).ConfigureAwait(false);
            var draft = await drafts.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Draft was not found.");
            _ = await drafts.BumpDraftRevisionAsync(
                new AgentDefinitionDraftRevisionBump(draftId, draft.Revision, time.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            return list;
        }

        public ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
            AgentDefinitionDraftResourceUpsert upsert,
            CancellationToken cancellationToken = default) =>
            inner.UpsertDraftResourceAsync(upsert, cancellationToken);

        public ValueTask<AgentDefinitionDraftResourceBatchBound> BindDraftResourcesAsync(
            AgentDefinitionDraftResourceBatchBind bind,
            CancellationToken cancellationToken = default) =>
            inner.BindDraftResourcesAsync(bind, cancellationToken);

        public ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
            AgentDefinitionDraftResourceRemove remove,
            CancellationToken cancellationToken = default) =>
            inner.RemoveDraftResourceAsync(remove, cancellationToken);

        public ValueTask<byte[]?> ReadDraftResourceContentAsync(
            Guid draftId,
            Guid resourceId,
            CancellationToken cancellationToken = default) =>
            inner.ReadDraftResourceContentAsync(draftId, resourceId, cancellationToken);

        public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
            string definitionId,
            int version,
            CancellationToken cancellationToken = default) =>
            inner.ListPublicationResourcesAsync(definitionId, version, cancellationToken);

        public ValueTask<byte[]?> ReadPublicationResourceContentAsync(
            string definitionId,
            int version,
            Guid resourceId,
            CancellationToken cancellationToken = default) =>
            inner.ReadPublicationResourceContentAsync(definitionId, version, resourceId, cancellationToken);
    }
}
