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

public sealed class InMemoryAdminDeprecationHistoryTests : AdminDeprecationHistoryTests
{
    protected override Task ForEachProfileAsync(Func<DeprecationHistoryFixture, Task> exercise)
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var events = new InMemoryAdminEventStore(ids);
        admin.EventStore = events;
        return exercise(new DeprecationHistoryFixture(admin, events, null));
    }

    [Fact]
    public async Task Deprecate_rolls_back_when_history_append_fails()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                AdminPublicationHistoryTests.SampleCandidateStatic("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draft.DraftId,
                draft.Revision,
                [],
                now.AddMinutes(1)),
            CancellationToken.None);
        admin.EventStore = new ThrowingAdminEventStore(ids);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.DeprecatePublicationAsync(
                new AgentDefinitionPublicationDeprecate(
                    "examiner",
                    published.Version,
                    published.MetadataRevision,
                    now.AddMinutes(2),
                    Guid.Parse("019944af-00d1-7000-8000-000000000306")),
                CancellationToken.None).AsTask());
        var refreshed = await admin.GetPublicationAsync("examiner", published.Version);
        Assert.NotNull(refreshed);
        Assert.Equal(DefinitionPublicationStatus.Active, refreshed!.Status);
        Assert.Equal(published.MetadataRevision, refreshed.MetadataRevision);
    }

    [Fact]
    public async Task Retry_deprecate_waits_on_publication_lock_during_failed_append()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var firstAppendHolding = new ManualResetEventSlim(false);
        var releaseFirstAppend = new ManualResetEventSlim(false);
        var events = new HaltedThrowOnceAdminEventStore(ids)
        {
            FirstDeprecateAppendHolding = firstAppendHolding,
            ReleaseFirstDeprecateAppend = releaseFirstAppend
        };
        admin.EventStore = events;
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                AdminPublicationHistoryTests.SampleCandidateStatic("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now),
            CancellationToken.None);
        var published = await admin.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draft.DraftId,
                draft.Revision,
                [],
                now.AddMinutes(1)),
            CancellationToken.None);

        var deprecate = new AgentDefinitionPublicationDeprecate(
            "examiner",
            published.Version,
            published.MetadataRevision,
            now.AddMinutes(2),
            Guid.Parse("019944af-00d1-7000-8000-000000000308"));
        var first = Task.Run(() =>
            admin.DeprecatePublicationAsync(deprecate, CancellationToken.None).AsTask());
        Assert.True(firstAppendHolding.Wait(TimeSpan.FromSeconds(5)));

        var second = Task.Run(() =>
            admin.DeprecatePublicationAsync(
                deprecate with { OperationId = Guid.Parse("019944af-00d1-7000-8000-000000000311") },
                CancellationToken.None).AsTask());
        await Task.Delay(100);
        Assert.False(second.IsCompleted);

        releaseFirstAppend.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var result = await second;
        Assert.Equal(DefinitionPublicationStatus.Deprecated, result.Status);
        var history = await events.ListAsync(
            new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
            CancellationToken.None);
        Assert.Single(history, item => item.Operation == AdminEventOperationKind.PublicationDeprecated);
    }
}

public sealed class SqliteAdminDeprecationHistoryTests : AdminDeprecationHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<DeprecationHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-deprecate-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new InMemoryDefinitionResourceContentStore();
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var events = new SqliteAdminEventStore(factory, ids);
            await exercise(new DeprecationHistoryFixture(admin, events, factory));
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

public abstract class AdminDeprecationHistoryTests
{
    protected sealed record DeprecationHistoryFixture(
        IAgentDefinitionAdminStore Admin,
        IAdminEventStore Events,
        IDbContextFactory<AgentCoreDbContext>? Factory);

    protected abstract Task ForEachProfileAsync(Func<DeprecationHistoryFixture, Task> exercise);

    [Fact]
    public async Task Deprecate_with_operation_id_records_publication_deprecated_event()
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
            var publishOperationId = Guid.Parse("019944af-00d1-7000-8000-000000000301");
            var published = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    publishOperationId,
                    ChangedSectionIds: ["instructions"]),
                CancellationToken.None);
            var deprecateOperationId = Guid.Parse("019944af-00d1-7000-8000-000000000302");
            var deprecated = await fixture.Admin.DeprecatePublicationAsync(
                new AgentDefinitionPublicationDeprecate(
                    "examiner",
                    published.Version,
                    published.MetadataRevision,
                    now.AddMinutes(2),
                    deprecateOperationId),
                CancellationToken.None);
            Assert.Equal(DefinitionPublicationStatus.Deprecated, deprecated.Status);
            Assert.Equal(2, deprecated.MetadataRevision);

            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
                CancellationToken.None);
            Assert.Equal(2, history.Count);
            var deprecatedEvent = history.Single(item => item.Operation == AdminEventOperationKind.PublicationDeprecated);
            Assert.Equal(deprecateOperationId, deprecatedEvent.OperationId);
            using var summary = JsonDocument.Parse(deprecatedEvent.SummaryJson);
            Assert.Equal(2, summary.RootElement.GetProperty("metadataRevision").GetInt64());
        });
    }

    [Fact]
    public async Task Stale_metadata_revision_does_not_record_deprecation_history()
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
            var published = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1)),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
                fixture.Admin.DeprecatePublicationAsync(
                    new AgentDefinitionPublicationDeprecate(
                        "examiner",
                        published.Version,
                        published.MetadataRevision - 1,
                        now.AddMinutes(2),
                        Guid.Parse("019944af-00d1-7000-8000-000000000310")),
                    CancellationToken.None).AsTask());
            Assert.Equal(409, error.StatusCode);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
                CancellationToken.None);
            Assert.DoesNotContain(history, item => item.Operation == AdminEventOperationKind.PublicationDeprecated);
        });
    }

    [Fact]
    public async Task Idempotent_deprecate_does_not_duplicate_history()
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
            var published = await fixture.Admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    [],
                    now.AddMinutes(1),
                    Guid.Parse("019944af-00d1-7000-8000-000000000303"),
                    ChangedSectionIds: ["instructions"]),
                CancellationToken.None);
            var deprecate = new AgentDefinitionPublicationDeprecate(
                "examiner",
                published.Version,
                published.MetadataRevision,
                now.AddMinutes(2),
                Guid.Parse("019944af-00d1-7000-8000-000000000304"));
            await fixture.Admin.DeprecatePublicationAsync(deprecate, CancellationToken.None);
            await fixture.Admin.DeprecatePublicationAsync(
                deprecate with { ExpectedMetadataRevision = 2 },
                CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.publication", $"examiner:{published.Version}"),
                CancellationToken.None);
            Assert.Single(history, item => item.Operation == AdminEventOperationKind.PublicationDeprecated);
        });
    }

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
        AdminPublicationHistoryTests.SampleCandidateStatic(definitionId);
}

internal sealed class HaltedThrowOnceAdminEventStore(IIdGenerator ids) : InMemoryAdminEventStore(ids)
{
    internal ManualResetEventSlim? FirstDeprecateAppendHolding { get; set; }

    internal ManualResetEventSlim? ReleaseFirstDeprecateAppend { get; set; }

    private int _deprecateAppendAttempts;

    internal override void AppendWithinLock(AdminEventAppend append)
    {
        if (append.Operation != AdminEventOperationKind.PublicationDeprecated)
        {
            base.AppendWithinLock(append);
            return;
        }

        if (Interlocked.Increment(ref _deprecateAppendAttempts) == 1)
        {
            FirstDeprecateAppendHolding?.Set();
            if (ReleaseFirstDeprecateAppend is null
                || !ReleaseFirstDeprecateAppend.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Admin history append synchronization timed out.");
            }

            throw new InvalidOperationException("Admin history append failed.");
        }

        base.AppendWithinLock(append);
    }
}
