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

public sealed class AdminDefinitionDraftValidationServiceTests
{
    [Fact]
    public async Task ValidateDraftAsync_rejects_when_draft_revision_changes_during_resource_resolution()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var innerResources = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var resourcesStore = new BumpDraftRevisionOnListResourceStore(innerResources, admin, clock);
        var validation = CreateValidationService(admin, resourcesStore, clock);

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
            validation.ValidateDraftAsync(draft.DraftId, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task ValidateDraftAsync_returns_revision_aligned_with_resource_bindings()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var validation = CreateValidationService(admin, resourcesStore, clock);

        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner);
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var result = await validation.ValidateDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.Equal(draft.Revision, result.DraftRevision);
        Assert.False(result.HasBlockingFindings);
    }

    private static AgentDefinitionDraftValidationService CreateValidationService(
        IAgentDefinitionAdminStore admin,
        IAgentDefinitionResourceAdminStore resourcesStore,
        FakeTimeProvider clock)
    {
        var content = new InMemoryDefinitionResourceContentStore();
        var builtIns = new VersionedBuiltInDefinitions(SampleDefinitions.Examiner);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock);
        var resources = new AgentDefinitionResourceService(admin, resourcesStore, content, clock);
        return new AgentDefinitionDraftValidationService(
            lifecycle,
            resources,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
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
