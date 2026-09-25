using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Workspaces;

namespace AgentCore.Infrastructure.Tests;

public sealed class DefinitionPublicationResourceWorkspaceTests
{
    [Fact]
    public async Task Publication_resources_are_listed_and_read_under_agent_resources()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var reader = new DefinitionPublicationResourceReader(resources);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, publicationResources: reader);

        var candidate = SampleCandidate("resource-agent");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate("resource-agent", candidate, DefinitionDraftSourceKind.New, null, now),
            CancellationToken.None);
        var bytes = "pinned-bytes"u8.ToArray();
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                "refs/note.txt",
                AgentDefinitionResourceKind.Reference,
                "text/plain",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [],
                now.AddMinutes(2)),
            CancellationToken.None);
        var definition = published.Payload;
        var session = Guid.CreateVersion7();

        await workspace.EnsureAsync(session, definition);
        var root = await workspace.ListAsync(session, definition, "/agent");
        Assert.Contains(root, node => node.LogicalPath == "/agent/resources");

        var refsDir = await workspace.ListAsync(session, definition, "/agent/resources");
        Assert.Contains(refsDir, node => node.LogicalPath == "/agent/resources/refs" && node.Directory);
        var files = await workspace.ListAsync(session, definition, "/agent/resources/refs");
        Assert.Contains(files, node => node.LogicalPath == "/agent/resources/refs/note.txt" && !node.Directory);

        var read = await workspace.ReadAsync(session, definition, "/agent/resources/refs/note.txt");
        Assert.Equal("pinned-bytes", System.Text.Encoding.UTF8.GetString(read.Bytes));
    }

    [Fact]
    public async Task Template_publication_resource_seeds_workspace_working_once()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var reader = new DefinitionPublicationResourceReader(resources);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, publicationResources: reader);

        const string templateId = "seed-pack";
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "resource-agent",
                SampleCandidate("resource-agent", templateId),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var bytes = "seed-me"u8.ToArray();
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        var otherBytes = "skip-me"u8.ToArray();
        var otherHash = DefinitionResourceContentHasher.ComputeSha256Hex(otherBytes);
        await content.StoreVerifiedAsync(otherHash, otherBytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                $"{templateId}/welcome.txt",
                AgentDefinitionResourceKind.Template,
                "text/plain",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterFirst = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draftAfterFirst!.DraftId,
                draftAfterFirst.Revision,
                null,
                "other-pack/extra.txt",
                AgentDefinitionResourceKind.Template,
                "text/plain",
                otherHash,
                otherBytes.Length,
                now.AddMinutes(2)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [],
                now.AddMinutes(3)),
            CancellationToken.None);
        var session = Guid.CreateVersion7();
        await workspace.EnsureAsync(session, published.Payload);
        var seeded = await workspace.ReadAsync(session, published.Payload, "/workspace/working/welcome.txt");
        Assert.Equal("seed-me", System.Text.Encoding.UTF8.GetString(seeded.Bytes));
        var skipped = await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, published.Payload, "/workspace/working/extra.txt").AsTask());
        Assert.Equal("NotFound", skipped.Code);
    }

    [Fact]
    public async Task Workspace_mutation_does_not_write_back_to_publication_resources()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var reader = new DefinitionPublicationResourceReader(resources);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, publicationResources: reader);

        const string templateId = "seed-pack";
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "resource-agent",
                SampleCandidate("resource-agent", templateId),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var bytes = "immutable-seed"u8.ToArray();
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                $"{templateId}/welcome.txt",
                AgentDefinitionResourceKind.Template,
                "text/plain",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [],
                now.AddMinutes(2)),
            CancellationToken.None);
        var session = Guid.CreateVersion7();
        await workspace.EnsureAsync(session, published.Payload);
        await workspace.WriteAsync(session, "/workspace/working/welcome.txt", "user-edited"u8.ToArray());
        await workspace.WriteAsync(session, "/workspace/working/local-only.txt", "session-owned"u8.ToArray());
        await workspace.EnsureAsync(session, published.Payload);

        var publicationBindings = await resources.ListPublicationResourcesAsync("resource-agent", published.Version);
        Assert.Single(publicationBindings);
        var publicationBytes = await resources.ReadPublicationResourceContentAsync(
            "resource-agent",
            published.Version,
            publicationBindings[0].ResourceId);
        Assert.Equal(bytes, publicationBytes);
        var seeded = await workspace.ReadAsync(session, published.Payload, "/workspace/working/welcome.txt");
        Assert.Equal("user-edited", System.Text.Encoding.UTF8.GetString(seeded.Bytes));
    }

    [Fact]
    public async Task Publication_template_overwrites_builtin_template_at_same_path()
    {
        using var dir = new TempDir();
        const string templateId = "seed-pack";
        var builtinDir = Path.Combine(dir.TemplateRoot, templateId);
        Directory.CreateDirectory(builtinDir);
        await File.WriteAllTextAsync(Path.Combine(builtinDir, "welcome.txt"), "builtin");

        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var reader = new DefinitionPublicationResourceReader(resources);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, publicationResources: reader);

        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "resource-agent",
                SampleCandidate("resource-agent", templateId),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var bytes = "durable"u8.ToArray();
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                $"{templateId}/welcome.txt",
                AgentDefinitionResourceKind.Template,
                "text/plain",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [],
                now.AddMinutes(2)),
            CancellationToken.None);
        var session = Guid.CreateVersion7();
        await workspace.EnsureAsync(session, published.Payload);
        var seeded = await workspace.ReadAsync(session, published.Payload, "/workspace/working/welcome.txt");
        Assert.Equal("durable", System.Text.Encoding.UTF8.GetString(seeded.Bytes));
    }

    [Fact]
    public async Task Template_publication_resource_without_workspace_policy_does_not_seed()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        var reader = new DefinitionPublicationResourceReader(resources);
        var workspace = new FileSessionWorkspace(dir.WorkspaceRoot, dir.TemplateRoot, publicationResources: reader);

        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate("resource-agent", SampleCandidate("resource-agent"), DefinitionDraftSourceKind.New, null, now),
            CancellationToken.None);
        var bytes = "seed-me"u8.ToArray();
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                "seed-pack/welcome.txt",
                AgentDefinitionResourceKind.Template,
                "text/plain",
                hash,
                bytes.Length,
                now.AddMinutes(1)),
            CancellationToken.None);
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draftAfterResource!.DraftId,
                draftAfterResource.Revision,
                [],
                now.AddMinutes(2)),
            CancellationToken.None);
        var session = Guid.CreateVersion7();
        await workspace.EnsureAsync(session, published.Payload);
        var missing = await Assert.ThrowsAsync<AgentCoreException>(() =>
            workspace.ReadAsync(session, published.Payload, "/workspace/working/welcome.txt").AsTask());
        Assert.Equal("NotFound", missing.Code);
    }

    private static AgentDefinitionCandidate SampleCandidate(string definitionId, string? workspaceTemplateId = null) =>
        new(
            1,
            definitionId,
            new AgentIdentity("Demo", "Guide", "Helps with demos.", "Calm"),
            ["Help the user"],
            "You are a demo agent.",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 512),
            new InitiativePolicy(false, 30_000, 60_000, 1, []),
            new VoiceConfiguration(false, "alloy", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>(),
            workspaceTemplateId is null ? null : new RoleEnvironment(Workspace: new WorkspaceTemplatePolicy(workspaceTemplateId)));

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Root = Path.Combine(Path.GetTempPath(), "agent-core-ws-res", Guid.NewGuid().ToString("N"));
            WorkspaceRoot = Path.Combine(Root, "workspaces");
            TemplateRoot = Path.Combine(Root, "templates");
            Directory.CreateDirectory(WorkspaceRoot);
            Directory.CreateDirectory(TemplateRoot);
        }

        public string Root { get; }
        public string WorkspaceRoot { get; }
        public string TemplateRoot { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }
}
