using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed class InMemoryAdminPublicationHistoryTests : AdminPublicationHistoryTests
{
    protected override Task ForEachProfileAsync(Func<PublicationHistoryFixture, Task> exercise)
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var events = new InMemoryAdminEventStore(ids);
        admin.EventStore = events;
        return exercise(new PublicationHistoryFixture(admin, events, null));
    }

    [Fact]
    public async Task Publish_rolls_back_when_history_append_fails()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        admin.EventStore = new ThrowingAdminEventStore(ids);
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                SampleCandidateStatic("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    Guid.Parse("019944af-00d1-7000-8000-000000000204"),
                    ChangedSectionIds: ["instructions"]),
                CancellationToken.None).AsTask());
        Assert.Null(await admin.GetPublicationAsync("examiner", 1));
        var refreshed = await admin.GetDraftAsync(draft.DraftId);
        Assert.NotNull(refreshed);
        Assert.Equal(draft.Revision, refreshed.Revision);
    }

    [Fact]
    public async Task Publish_rolls_back_publication_resources_when_history_append_fails()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var content = new InMemoryDefinitionResourceContentStore();
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var resources = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        admin.ResourceStore = resources;
        admin.EventStore = new ThrowingAdminEventStore(ids);
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                SampleCandidateStatic("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var bytes = "history-rollback"u8.ToArray();
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
        var draftAfterResource = await admin.GetDraftAsync(draft.DraftId);
        Assert.NotNull(draftAfterResource);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draftAfterResource.DraftId,
                    draftAfterResource.Revision,
                    [],
                    now.AddMinutes(2),
                    Guid.Parse("019944af-00d1-7000-8000-000000000206"),
                    ChangedSectionIds: ["resources"]),
                CancellationToken.None).AsTask());
        Assert.Null(await admin.GetPublicationAsync("examiner", 1));
        var publicationResources = await resources.ListPublicationResourcesAsync("examiner", 1, CancellationToken.None);
        Assert.Empty(publicationResources);
    }

    [Fact]
    public async Task Publish_requires_event_store_when_operation_id_present()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = null };
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                SampleCandidateStatic("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    Guid.Parse("019944af-00d1-7000-8000-000000000205"),
                    ChangedSectionIds: ["instructions"]),
                CancellationToken.None).AsTask());
        Assert.Equal(400, error.StatusCode);
    }
}

public sealed class SqliteAdminPublicationHistoryTests : AdminPublicationHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<PublicationHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-pub-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new InMemoryDefinitionResourceContentStore();
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var events = new SqliteAdminEventStore(factory, ids);
            await exercise(new PublicationHistoryFixture(admin, events, factory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}

public abstract class AdminPublicationHistoryTests
{
    protected sealed record PublicationHistoryFixture(
        IAgentDefinitionAdminStore Admin,
        IAdminEventStore Events,
        IDbContextFactory<AgentCoreDbContext>? Factory);

    protected abstract Task ForEachProfileAsync(Func<PublicationHistoryFixture, Task> exercise);

    internal static AgentDefinitionCandidate SampleCandidateStatic(string definitionId) =>
        SampleCandidate(definitionId);

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
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

    [Fact]
    public async Task Publish_with_operation_id_records_changed_sections_with_publication()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var candidate = SampleCandidate("examiner") with
            {
                SystemInstructions = SampleCandidate("examiner").SystemInstructions + "\nHistory marker."
            };
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    candidate,
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000201");
            var published = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    operationId,
                    ChangedSectionIds: ["instructions", "triggerPolicy"]),
                CancellationToken.None);

            var loaded = await fixture.Admin.GetPublicationAsync("examiner", published.Version);
            Assert.NotNull(loaded);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
                CancellationToken.None);
            Assert.Single(history);
            using var summary = JsonDocument.Parse(history[0].SummaryJson);
            Assert.True(summary.RootElement.TryGetProperty("changedSections", out var sections));
            Assert.Contains(
                sections.EnumerateArray(),
                item => item.GetString() == "instructions");
        });
    }

    [Fact]
    public async Task Failed_publish_does_not_record_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    SampleCandidate("examiner"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000202");
            await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Admin.PublishDraftAsync(
                    new AgentDefinitionDraftPublish(
                        draft.DraftId,
                        draft.Revision - 1,
                        [],
                        now.AddMinutes(1),
                        operationId,
                        ChangedSectionIds: ["instructions"]),
                    CancellationToken.None).AsTask());
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Empty(history);
        });
    }

}

internal sealed class ThrowingAdminEventStore(IIdGenerator ids) : InMemoryAdminEventStore(ids)
{
    internal override void AppendWithinLock(AdminEventAppend append) =>
        throw new InvalidOperationException("Admin history append failed.");
}

public sealed class SqliteAdminPublicationHistoryReopenTests
{
    [Fact]
    public async Task Sqlite_reopen_preserves_publication_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-pub-history-reopen-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new ReopenSqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new InMemoryDefinitionResourceContentStore();
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    AdminPublicationHistoryTests.SampleCandidateStatic("examiner"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000203");
            var published = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    operationId,
                    ChangedSectionIds: ["capabilities"]),
                CancellationToken.None);

            var reopenedEvents = new SqliteAdminEventStore(factory, new SystemIdGenerator(TimeProvider.System));
            var history = await reopenedEvents.ListAsync(
                new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
                CancellationToken.None);
            Assert.Single(history);
            Assert.Equal(operationId, history[0].OperationId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class ReopenSqliteContextFactory(DbContextOptions<AgentCoreDbContext> options)
        : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
