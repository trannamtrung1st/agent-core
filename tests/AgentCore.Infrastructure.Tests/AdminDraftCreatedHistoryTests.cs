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

public sealed class InMemoryAdminDraftCreatedHistoryTests : AdminDraftCreatedHistoryTests
{
    protected override Task ForEachProfileAsync(Func<DraftCreatedHistoryFixture, Task> exercise)
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var events = new InMemoryAdminEventStore(ids);
        admin.EventStore = events;
        return exercise(new DraftCreatedHistoryFixture(admin, events));
    }

    [Fact]
    public async Task Concurrent_retry_waits_on_operation_lock_during_draft_created_append()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var firstAppendHolding = new ManualResetEventSlim(false);
        var releaseFirstAppend = new ManualResetEventSlim(false);
        var events = new HaltedDraftCreatedAdminEventStore(ids)
        {
            FirstDraftCreatedAppendHolding = firstAppendHolding,
            ReleaseFirstDraftCreatedAppend = releaseFirstAppend
        };
        admin.EventStore = events;
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        const string definitionId = "examiner-halted-draft";
        var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000406");
        var create = new AgentDefinitionDraftCreate(
            definitionId,
            AdminPublicationHistoryTests.SampleCandidateStatic(definitionId),
            DefinitionDraftSourceKind.New,
            null,
            now,
            operationId);
        var first = Task.Run(() => admin.CreateDraftAsync(create, CancellationToken.None).AsTask());
        Assert.True(firstAppendHolding.Wait(TimeSpan.FromSeconds(5)));
        var retryEnteredCreate = new ManualResetEventSlim(false);
        var second = Task.Run(() =>
        {
            retryEnteredCreate.Set();
            return admin.CreateDraftAsync(create, CancellationToken.None).AsTask();
        });
        Assert.True(retryEnteredCreate.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(second.IsCompleted);
        releaseFirstAppend.Set();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].DraftId, results[1].DraftId);
        var drafts = await admin.ListDraftsAsync(CancellationToken.None);
        Assert.Single(drafts, item => item.DefinitionId == definitionId);
        var history = await events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
        Assert.Single(history, item => item.Operation == AdminEventOperationKind.DraftCreated);
    }

    [Fact]
    public async Task Create_rolls_back_when_history_append_fails()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        admin.EventStore = new ThrowingAdminEventStore(ids);
        var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    AdminPublicationHistoryTests.SampleCandidateStatic("examiner"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now,
                    Guid.Parse("019944af-00d1-7000-8000-000000000401")),
                CancellationToken.None).AsTask());
        var drafts = await admin.ListDraftsAsync(CancellationToken.None);
        Assert.Empty(drafts);
    }
}

public sealed class SqliteAdminDraftCreatedHistoryTests : AdminDraftCreatedHistoryTests
{
    protected override async Task ForEachProfileAsync(Func<DraftCreatedHistoryFixture, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-draft-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new InMemoryDefinitionResourceContentStore();
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var events = new SqliteAdminEventStore(factory, ids);
            await exercise(new DraftCreatedHistoryFixture(admin, events));
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

public abstract class AdminDraftCreatedHistoryTests
{
    protected sealed record DraftCreatedHistoryFixture(IAgentDefinitionAdminStore Admin, IAdminEventStore Events);

    protected abstract Task ForEachProfileAsync(Func<DraftCreatedHistoryFixture, Task> exercise);

    [Fact]
    public async Task Create_with_operation_id_records_draft_created_event()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000402");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    SampleCandidate("examiner"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now,
                    operationId),
                CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.draft", draft.DraftId.ToString("D")),
                CancellationToken.None);
            Assert.Single(history);
            Assert.Equal(operationId, history[0].OperationId);
            Assert.Equal(AdminEventOperationKind.DraftCreated, history[0].Operation);
            using var summary = JsonDocument.Parse(history[0].SummaryJson);
            Assert.Equal("New", summary.RootElement.GetProperty("sourceKind").GetString());
            Assert.Equal(JsonValueKind.Null, summary.RootElement.GetProperty("sourceVersion").ValueKind);
        });
    }

    [Fact]
    public async Task Retry_with_same_operation_id_returns_same_draft_without_duplicate_history()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000404");
            var create = new AgentDefinitionDraftCreate(
                "examiner",
                SampleCandidate("examiner"),
                DefinitionDraftSourceKind.New,
                null,
                now,
                operationId);
            var first = await fixture.Admin.CreateDraftAsync(create, CancellationToken.None);
            var second = await fixture.Admin.CreateDraftAsync(create, CancellationToken.None);
            Assert.Equal(first.DraftId, second.DraftId);
            await AssertSinglePersistedDraftAsync(fixture.Admin, "examiner", first.DraftId);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(history, item => item.Operation == AdminEventOperationKind.DraftCreated);
            Assert.Equal(operationId, history[0].OperationId);
        });
    }

    [Fact]
    public async Task Concurrent_retry_with_same_operation_id_persists_one_draft()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            const string definitionId = "examiner-concurrent-draft";
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000405");
            var create = new AgentDefinitionDraftCreate(
                definitionId,
                SampleCandidate(definitionId),
                DefinitionDraftSourceKind.New,
                null,
                now,
                operationId);
            var startBarrier = new Barrier(2);
            var first = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return fixture.Admin.CreateDraftAsync(create, CancellationToken.None).AsTask();
            });
            var second = Task.Run(() =>
            {
                startBarrier.SignalAndWait();
                return fixture.Admin.CreateDraftAsync(create, CancellationToken.None).AsTask();
            });
            var results = await Task.WhenAll(first, second);
            Assert.Equal(results[0].DraftId, results[1].DraftId);
            await AssertSinglePersistedDraftAsync(fixture.Admin, definitionId, results[0].DraftId);
            var history = await fixture.Events.ListAsync(new AdminEventListQuery(), CancellationToken.None);
            Assert.Single(
                history,
                item => item.Operation == AdminEventOperationKind.DraftCreated && item.OperationId == operationId);
        });
    }

    [Fact]
    public async Task Fork_create_records_source_version_in_summary()
    {
        await ForEachProfileAsync(async fixture =>
        {
            var now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
            var operationId = Guid.Parse("019944af-00d1-7000-8000-000000000403");
            var draft = await fixture.Admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    SampleCandidate("examiner"),
                    DefinitionDraftSourceKind.ForkBuiltIn,
                    1,
                    now,
                    operationId),
                CancellationToken.None);
            var history = await fixture.Events.ListAsync(
                new AdminEventListQuery("definition.draft", draft.DraftId.ToString("D")),
                CancellationToken.None);
            Assert.Single(history);
            using var summary = JsonDocument.Parse(history[0].SummaryJson);
            Assert.Equal("ForkBuiltIn", summary.RootElement.GetProperty("sourceKind").GetString());
            Assert.Equal(1, summary.RootElement.GetProperty("sourceVersion").GetInt32());
        });
    }

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
        AdminPublicationHistoryTests.SampleCandidateStatic(definitionId);

    private static async Task AssertSinglePersistedDraftAsync(
        IAgentDefinitionAdminStore admin,
        string definitionId,
        Guid expectedDraftId)
    {
        var drafts = await admin.ListDraftsAsync(CancellationToken.None);
        var matches = drafts.Where(item => item.DefinitionId == definitionId).ToArray();
        Assert.Single(matches);
        Assert.Equal(expectedDraftId, matches[0].DraftId);
    }
}

internal sealed class HaltedDraftCreatedAdminEventStore(IIdGenerator ids) : InMemoryAdminEventStore(ids)
{
    internal ManualResetEventSlim? FirstDraftCreatedAppendHolding { get; set; }

    internal ManualResetEventSlim? ReleaseFirstDraftCreatedAppend { get; set; }

    private int _draftCreatedAppendAttempts;

    internal override void AppendWithinLock(AdminEventAppend append)
    {
        if (append.Operation != AdminEventOperationKind.DraftCreated)
        {
            base.AppendWithinLock(append);
            return;
        }

        if (Interlocked.Increment(ref _draftCreatedAppendAttempts) == 1)
        {
            FirstDraftCreatedAppendHolding?.Set();
            if (ReleaseFirstDraftCreatedAppend is null
                || !ReleaseFirstDraftCreatedAppend.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Draft created history append synchronization timed out.");
            }
        }

        base.AppendWithinLock(append);
    }
}
