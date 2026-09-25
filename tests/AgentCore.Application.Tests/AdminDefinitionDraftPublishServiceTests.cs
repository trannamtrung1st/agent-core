using System.Reflection;
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

public sealed class AdminDefinitionDraftPublishServiceTests
{
    [Fact]
    public async Task PublishDraftAsync_rejects_blocking_resource_validation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var publish = CreatePublishService(admin, resourcesStore, clock);

        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            Environment = (SampleDefinitions.Examiner.Environment ?? RoleEnvironment.Empty) with
            {
                KnowledgeSources =
                [
                    new KnowledgeSourceRef("policy", "Policy", "Missing binding")
                ]
            }
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            publish.PublishDraftAsync(draft.DraftId, draft.Revision, CancellationToken.None).AsTask());
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public void Lifecycle_service_does_not_expose_public_unvalidated_publish()
    {
        var publishMethod = typeof(AgentDefinitionLifecycleService)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(method => method.Name == "PublishDraftAsync");
        Assert.Null(publishMethod);
    }

    [Fact]
    public async Task PublishDraftAsync_rejects_stale_expected_revision()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var publish = CreatePublishService(admin, resourcesStore, clock);

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
            publish.PublishDraftAsync(draft.DraftId, draft.Revision - 1, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
    }

    private static AgentDefinitionDraftPublishService CreatePublishService(
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
        var ids = new SystemIdGenerator(clock);
        return new AgentDefinitionDraftPublishService(lifecycle, validation, evaluation, diff, ids);
    }

    private sealed class VersionedBuiltInDefinitions(AgentDefinition definition) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);

        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(
                string.Equals(id, definition.Id, StringComparison.Ordinal) ? definition : null);
    }
}
