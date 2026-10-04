using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class PublicationKnowledgeRetrieveTests
{
    [Fact]
    public async Task Knowledge_retrieve_reads_publication_resource_for_durable_definition_version()
    {
        var agentsDir = FindAgents();
        var builtIns = new FileAgentDefinitionStore(agentsDir, SyntheticProviderAliases.Default);
        var builtin = await builtIns.GetAsync("customer-support", 1, CancellationToken.None);
        Assert.NotNull(builtin);

        var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var publicationReader = new DefinitionPublicationResourceReader(resources);

        var candidate = AgentDefinitionCandidate.FromDefinition(builtin!) with
        {
            Environment = (builtin!.Environment ?? RoleEnvironment.Empty) with
            {
                KnowledgeSources =
                [
                    new KnowledgeSourceRef(
                        "support-order-policy",
                        "Simulated order policy",
                        "support-order-policy@demo")
                ]
            }
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "customer-support",
                candidate,
                DefinitionDraftSourceKind.ForkBuiltIn,
                1,
                now),
            CancellationToken.None);
        const string publishedPolicy = "Published-only order policy body for P7C regression.";
        var bytes = System.Text.Encoding.UTF8.GetBytes(publishedPolicy);
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                "knowledge/support-order-policy",
                AgentDefinitionResourceKind.Knowledge,
                "text/markdown",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [1, 2],
                now.AddMinutes(2)),
            CancellationToken.None);

        var resolver = new DefinitionBoundKnowledgeContentResolver(
            new FileApprovedKnowledgeCatalog(agentsDir),
            builtIns,
            admin,
            publicationReader);
        var knowledge = new RoleKnowledgeService(resolver, TimeProvider.System);

        var builtinDocument = await knowledge.RetrieveAsync(builtin!, "support-order-policy", CancellationToken.None);
        Assert.Contains("Simulated order policy", builtinDocument.Content, StringComparison.Ordinal);

        var durableDocument = await knowledge.RetrieveAsync(published.Payload, "support-order-policy", CancellationToken.None);
        Assert.Equal(publishedPolicy, durableDocument.Content);
        Assert.True(published.Version > builtin!.Version);
        Assert.Null(published.Payload.Environment!.KnowledgeList[0].ResourcePath);
    }

    [Fact]
    public async Task Knowledge_retrieve_reads_an_explicit_resource_path_instead_of_the_legacy_path()
    {
        var agentsDir = FindAgents();
        var builtIns = new FileAgentDefinitionStore(agentsDir, SyntheticProviderAliases.Default);
        var builtin = await builtIns.GetAsync("customer-support", 1, CancellationToken.None);
        Assert.NotNull(builtin);

        var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var publicationReader = new DefinitionPublicationResourceReader(resources);
        var candidate = AgentDefinitionCandidate.FromDefinition(builtin!) with
        {
            Environment = (builtin!.Environment ?? RoleEnvironment.Empty) with
            {
                KnowledgeSources =
                [
                    new KnowledgeSourceRef(
                        "support-order-policy",
                        "Simulated order policy",
                        "support-order-policy@demo",
                        "knowledge/refund-policy.md")
                ]
            }
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "customer-support",
                candidate,
                DefinitionDraftSourceKind.ForkBuiltIn,
                1,
                now),
            CancellationToken.None);
        await BindAsync(content, resources, draft, "knowledge/support-order-policy", "Legacy body", now.AddMinutes(1));
        var afterLegacy = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        await BindAsync(content, resources, afterLegacy!, "knowledge/refund-policy.md", "Explicit refund body", now.AddMinutes(2));
        var ready = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(ready!.DraftId, ready.Revision, [1, 2], now.AddMinutes(3)),
            CancellationToken.None);

        var resolver = new DefinitionBoundKnowledgeContentResolver(
            new FileApprovedKnowledgeCatalog(agentsDir),
            builtIns,
            admin,
            publicationReader);
        var document = await new RoleKnowledgeService(resolver, TimeProvider.System)
            .RetrieveAsync(published.Payload, "support-order-policy", CancellationToken.None);

        Assert.Equal("Explicit refund body", document.Content);
        Assert.Equal("knowledge/refund-policy.md", published.Payload.Environment!.KnowledgeList[0].ResourcePath);
    }

    private static async Task BindAsync(
        InMemoryDefinitionResourceContentStore content,
        InMemoryAgentDefinitionResourceAdminStore resources,
        AgentDefinitionDraft draft,
        string path,
        string body,
        DateTimeOffset updatedAt)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                path,
                AgentDefinitionResourceKind.Knowledge,
                "text/markdown",
                hash,
                bytes.Length,
                updatedAt),
            CancellationToken.None);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "customer-support.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
