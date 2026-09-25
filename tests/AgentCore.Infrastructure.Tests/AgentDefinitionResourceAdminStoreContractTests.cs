using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAgentDefinitionResourceAdminStoreContractTests : AgentDefinitionResourceAdminStoreContractTests
{
    protected override async Task ForEachStoresAsync(Func<ResourceStoreFixture, Task> exercise)
    {
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System));
        var resources = new InMemoryAgentDefinitionResourceAdminStore(
            admin,
            content,
            new SystemIdGenerator(TimeProvider.System));
        admin.ResourceStore = resources;
        await exercise(new ResourceStoreFixture(admin, resources, content));
    }

    [Fact]
    public async Task Concurrent_reads_do_not_throw_during_mutation()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-01-05T04:00:00Z");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "resource-agent",
                    SampleCandidate("resource-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var bytes = "stable"u8.ToArray();
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await fixture.Content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
            var resource = await fixture.Resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draft.DraftId,
                    draft.Revision,
                    null,
                    "refs/a.txt",
                    AgentDefinitionResourceKind.Reference,
                    "text/plain",
                    hash,
                    bytes.Length,
                    now),
                CancellationToken.None);
            using var gate = new ManualResetEventSlim(false);
            var reads = Task.Run(async () =>
            {
                gate.Wait();
                for (var i = 0; i < 200; i++)
                {
                    _ = await fixture.Resources.ListDraftResourcesAsync(draft.DraftId, CancellationToken.None);
                    _ = await fixture.Resources.ReadDraftResourceContentAsync(
                        draft.DraftId,
                        resource.ResourceId,
                        CancellationToken.None);
                }
            });
            var mutations = Task.Run(async () =>
            {
                gate.Set();
                for (var i = 0; i < 50; i++)
                {
                    var current = await fixture.Admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
                    var nextBytes = System.Text.Encoding.UTF8.GetBytes($"v{i}");
                    var nextHash = DefinitionResourceContentHasher.ComputeSha256Hex(nextBytes);
                    await fixture.Content.StoreVerifiedAsync(nextHash, nextBytes, CancellationToken.None);
                    try
                    {
                        _ = await fixture.Resources.UpsertDraftResourceAsync(
                            new AgentDefinitionDraftResourceUpsert(
                                draft.DraftId,
                                current!.Revision,
                                resource.ResourceId,
                                "refs/a.txt",
                                AgentDefinitionResourceKind.Reference,
                                "text/plain",
                                nextHash,
                                nextBytes.Length,
                                now.AddMinutes(i + 1)),
                            CancellationToken.None);
                    }
                    catch (AgentCoreException ex) when (ex.Code == "Conflict")
                    {
                    }
                }
            });
            await Task.WhenAll(reads, mutations);
        });
    }
}

public sealed class SqliteAgentDefinitionResourceAdminStoreContractTests : AgentDefinitionResourceAdminStoreContractTests
{
    [Fact]
    public async Task Reopen_preserves_publication_resource_bindings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-def-resources-reopen-{Guid.NewGuid():N}.db");
        var blobRoot = Path.Combine(Path.GetTempPath(), $"agent-core-def-resource-blobs-reopen-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        int publishedV1Version;
        int publishedV2Version;
        Guid resourceId;
        byte[] bytesV1;
        byte[] bytesV2;
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var (admin, resources, content) = CreateSqliteStores(factory, blobRoot);
            var now = DateTimeOffset.Parse("2026-01-06T00:00:00Z");
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "reopen-resource-agent",
                    SampleCandidate("reopen-resource-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            bytesV1 = "version-one"u8.ToArray();
            var hashV1 = DefinitionResourceContentHasher.ComputeSha256Hex(bytesV1);
            await content.StoreVerifiedAsync(hashV1, bytesV1, CancellationToken.None);
            var resource = await resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draft.DraftId,
                    draft.Revision,
                    null,
                    "knowledge/policy.md",
                    AgentDefinitionResourceKind.Knowledge,
                    "text/markdown",
                    hashV1,
                    bytesV1.Length,
                    now.AddMinutes(1)),
                CancellationToken.None);
            resourceId = resource.ResourceId;
            var draftAfterResource = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            var publishedV1 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draftAfterResource!.DraftId,
                    draftAfterResource.Revision,
                    [],
                    now.AddMinutes(2)),
                CancellationToken.None);
            publishedV1Version = publishedV1.Version;
            var draftAfterPublishV1 = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);

            bytesV2 = "version-two"u8.ToArray();
            var hashV2 = DefinitionResourceContentHasher.ComputeSha256Hex(bytesV2);
            await content.StoreVerifiedAsync(hashV2, bytesV2, CancellationToken.None);
            _ = await resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draftAfterPublishV1!.DraftId,
                    draftAfterPublishV1.Revision,
                    resource.ResourceId,
                    "knowledge/policy.md",
                    AgentDefinitionResourceKind.Knowledge,
                    "text/markdown",
                    hashV2,
                    bytesV2.Length,
                    now.AddMinutes(3)),
                CancellationToken.None);
            var draftBeforeV2 = await admin.GetDraftAsync(draftAfterPublishV1.DraftId, CancellationToken.None);
            var publishedV2 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draftBeforeV2!.DraftId,
                    draftBeforeV2.Revision,
                    [publishedV1.Version],
                    now.AddMinutes(4)),
                CancellationToken.None);
            publishedV2Version = publishedV2.Version;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }

        try
        {
            var reopenedFactory = new SqliteContextFactory(options);
            var (_, reopenedResources, _) = CreateSqliteStores(reopenedFactory, blobRoot);
            var v1Bindings = await reopenedResources.ListPublicationResourcesAsync(
                "reopen-resource-agent",
                publishedV1Version);
            var v2Bindings = await reopenedResources.ListPublicationResourcesAsync(
                "reopen-resource-agent",
                publishedV2Version);
            Assert.Single(v1Bindings);
            Assert.Single(v2Bindings);
            var v1Read = await reopenedResources.ReadPublicationResourceContentAsync(
                "reopen-resource-agent",
                publishedV1Version,
                resourceId);
            var v2Read = await reopenedResources.ReadPublicationResourceContentAsync(
                "reopen-resource-agent",
                publishedV2Version,
                resourceId);
            Assert.Equal(bytesV1, v1Read);
            Assert.Equal(bytesV2, v2Read);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(blobRoot))
            {
                Directory.Delete(blobRoot, recursive: true);
            }
        }
    }

    private static (SqliteAgentDefinitionAdminStore Admin, SqliteAgentDefinitionResourceAdminStore Resources, FileDefinitionResourceContentStore Content)
        CreateSqliteStores(IDbContextFactory<AgentCoreDbContext> factory, string blobRoot)
    {
        var content = new FileDefinitionResourceContentStore(blobRoot);
        var admin = new SqliteAgentDefinitionAdminStore(
            factory,
            new SystemIdGenerator(TimeProvider.System),
            content);
        var resources = new SqliteAgentDefinitionResourceAdminStore(
            factory,
            content,
            new SystemIdGenerator(TimeProvider.System));
        return (admin, resources, content);
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }

    protected override async Task ForEachStoresAsync(Func<ResourceStoreFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-def-resources-{Guid.NewGuid():N}.db");
        var blobRoot = Path.Combine(Path.GetTempPath(), $"agent-core-def-resource-blobs-{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var (admin, resources, content) = CreateSqliteStores(factory, blobRoot);
            await exercise(new ResourceStoreFixture(admin, resources, content));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(blobRoot))
            {
                Directory.Delete(blobRoot, recursive: true);
            }
        }
    }
}

public abstract class AgentDefinitionResourceAdminStoreContractTests
{
    [Fact]
    public async Task Publish_binds_immutable_resource_snapshots_per_version()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-01-05T00:00:00Z");
            var candidate = SampleCandidate("resource-agent");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate("resource-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            var bytesV1 = "version-one"u8.ToArray();
            var hashV1 = DefinitionResourceContentHasher.ComputeSha256Hex(bytesV1);
            await fixture.Content.StoreVerifiedAsync(hashV1, bytesV1, CancellationToken.None);
            var resource = await fixture.Resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draft.DraftId,
                    draft.Revision,
                    null,
                    "knowledge/policy.md",
                    AgentDefinitionResourceKind.Knowledge,
                    "text/markdown",
                    hashV1,
                    bytesV1.Length,
                    now.AddMinutes(1)),
                CancellationToken.None);
            var draftAfterResource = await fixture.Admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            var publishedV1 = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draftAfterResource!.DraftId, draftAfterResource.Revision, [], now.AddMinutes(2)),
                CancellationToken.None);
            var draftAfterPublishV1 = await fixture.Admin.GetDraftAsync(draft.DraftId, CancellationToken.None);

            var bytesV2 = "version-two"u8.ToArray();
            var hashV2 = DefinitionResourceContentHasher.ComputeSha256Hex(bytesV2);
            await fixture.Content.StoreVerifiedAsync(hashV2, bytesV2, CancellationToken.None);
            _ = await fixture.Resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draftAfterPublishV1!.DraftId,
                    draftAfterPublishV1.Revision,
                    resource.ResourceId,
                    "knowledge/policy.md",
                    AgentDefinitionResourceKind.Knowledge,
                    "text/markdown",
                    hashV2,
                    bytesV2.Length,
                    now.AddMinutes(3)),
                CancellationToken.None);
            var draftBeforeV2 = await fixture.Admin.GetDraftAsync(draftAfterPublishV1!.DraftId, CancellationToken.None);
            var publishedV2 = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draftBeforeV2!.DraftId, draftBeforeV2.Revision, [publishedV1.Version], now.AddMinutes(4)),
                CancellationToken.None);

            var v1Bindings = await fixture.Resources.ListPublicationResourcesAsync("resource-agent", publishedV1.Version);
            var v2Bindings = await fixture.Resources.ListPublicationResourcesAsync("resource-agent", publishedV2.Version);
            Assert.Single(v1Bindings);
            Assert.Single(v2Bindings);
            Assert.Equal(hashV1, v1Bindings[0].ContentSha256);
            Assert.Equal(hashV2, v2Bindings[0].ContentSha256);
            var v1Bytes = await fixture.Resources.ReadPublicationResourceContentAsync(
                "resource-agent",
                publishedV1.Version,
                v1Bindings[0].ResourceId);
            var v2Bytes = await fixture.Resources.ReadPublicationResourceContentAsync(
                "resource-agent",
                publishedV2.Version,
                v2Bindings[0].ResourceId);
            Assert.Equal(bytesV1, v1Bytes);
            Assert.Equal(bytesV2, v2Bytes);
        });
    }

    [Fact]
    public async Task Content_store_enforces_hash_integrity_and_defensive_reads()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var bytes = "hello"u8.ToArray();
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await fixture.Content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Content.StoreVerifiedAsync(hash, "other"u8.ToArray(), CancellationToken.None));
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Content.StoreVerifiedAsync(
                    DefinitionResourceContentHasher.ComputeSha256Hex("wrong"u8.ToArray()),
                    bytes,
                    CancellationToken.None));

            var read = await fixture.Content.ReadAsync(hash, CancellationToken.None);
            Assert.NotNull(read);
            read![0] = (byte)(read[0] + 1);
            var readAgain = await fixture.Content.ReadAsync(hash, CancellationToken.None);
            Assert.Equal(bytes, readAgain);
        });
    }

    [Fact]
    public async Task Upsert_rejects_missing_or_mismatched_content()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-01-05T02:00:00Z");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "resource-agent",
                    SampleCandidate("resource-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var bytes = "bound"u8.ToArray();
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "refs/note.txt",
                        AgentDefinitionResourceKind.Reference,
                        "text/plain",
                        hash,
                        bytes.Length,
                        now),
                    CancellationToken.None));
            await fixture.Content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "refs/note.txt",
                        AgentDefinitionResourceKind.Reference,
                        "text/plain",
                        hash,
                        bytes.Length + 1,
                        now),
                    CancellationToken.None));
        });
    }

    [Fact]
    public async Task Rejects_padded_media_type_and_textual_secrets()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-01-05T03:00:00Z");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "resource-agent",
                    SampleCandidate("resource-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var secretBytes = "OPENAI_API_KEY=abc"u8.ToArray();
            var secretHash = DefinitionResourceContentHasher.ComputeSha256Hex(secretBytes);
            await fixture.Content.StoreVerifiedAsync(secretHash, secretBytes, CancellationToken.None);
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "refs/secret.txt",
                        AgentDefinitionResourceKind.Reference,
                        " text/plain ",
                        secretHash,
                        secretBytes.Length,
                        now),
                    CancellationToken.None));
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "refs/secret.txt",
                        AgentDefinitionResourceKind.Reference,
                        "text/plain",
                        secretHash,
                        secretBytes.Length,
                        now),
                    CancellationToken.None));
        });
    }

    [Fact]
    public async Task Rejects_traversal_and_forbidden_logical_paths()
    {
        await ForEachStoresAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-01-05T01:00:00Z");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "resource-agent",
                    SampleCandidate("resource-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var bytes = "ok"u8.ToArray();
            var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
            await fixture.Content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "../secrets.txt",
                        AgentDefinitionResourceKind.Reference,
                        "text/plain",
                        hash,
                        bytes.Length,
                        now),
                    CancellationToken.None));
            await Assert.ThrowsAsync<AgentCoreException>(async () =>
                await fixture.Resources.UpsertDraftResourceAsync(
                    new AgentDefinitionDraftResourceUpsert(
                        draft.DraftId,
                        draft.Revision,
                        null,
                        "agents/builtin.md",
                        AgentDefinitionResourceKind.Reference,
                        "text/plain",
                        hash,
                        bytes.Length,
                        now),
                    CancellationToken.None));
        });
    }

    protected abstract Task ForEachStoresAsync(Func<ResourceStoreFixture, Task> exercise);

    protected sealed record ResourceStoreFixture(
        IAgentDefinitionAdminStore Admin,
        IAgentDefinitionResourceAdminStore Resources,
        IDefinitionResourceContentStore Content);

    protected static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
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
            new Dictionary<string, string>());
}
