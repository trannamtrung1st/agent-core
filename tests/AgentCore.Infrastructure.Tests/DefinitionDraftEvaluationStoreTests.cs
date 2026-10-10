using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Tests;

public sealed class DefinitionDraftEvaluationStoreTests
{
    [Fact]
    public async Task InMemory_upsert_leaves_scenario_unchanged_when_revision_bump_fails()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var inner = new InMemoryAgentDefinitionAdminStore(ids);
        var admin = new FailingBumpAgentDefinitionAdminStore(inner);
        var store = new InMemoryDefinitionDraftEvaluationStore(admin);
        var draft = await inner.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "eval-agent",
                SampleCandidate("eval-agent"),
                DefinitionDraftSourceKind.New,
                null,
                DateTimeOffset.Parse("2026-09-25T12:00:00Z")),
            CancellationToken.None);

        var upsert = new DefinitionEvaluationScenarioUpsert(
            draft.DraftId,
            "scenario-a",
            "Title",
            "Prompt",
            DefinitionEvaluationRequirementLevel.Advisory,
            DefinitionEvaluationCheckType.ToolOffered,
            "tool",
            DateTimeOffset.Parse("2026-09-25T12:01:00Z"));

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, upsert, CancellationToken.None).AsTask());
        Assert.Equal(409, error.StatusCode);
        Assert.Empty(await store.ListScenariosAsync(draft.DraftId, CancellationToken.None));
        var unchanged = await inner.GetDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.Equal(draft.Revision, unchanged!.Revision);
    }

    [Fact]
    public async Task InMemory_concurrent_save_and_latest_result_do_not_throw()
    {
        var ids = new SystemIdGenerator(TimeProvider.System);
        var admin = new InMemoryAgentDefinitionAdminStore(ids);
        var store = new InMemoryDefinitionDraftEvaluationStore(admin);
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "eval-agent",
                SampleCandidate("eval-agent"),
                DefinitionDraftSourceKind.New,
                null,
                DateTimeOffset.Parse("2026-09-25T12:00:00Z")),
            CancellationToken.None);
        var recordedAt = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        using var gate = new ManualResetEventSlim(false);
        var reads = Task.Run(async () =>
        {
            gate.Wait();
            for (var i = 0; i < 500; i++)
            {
                _ = await store.GetLatestResultAsync(draft.DraftId, "scenario-a", CancellationToken.None);
            }
        });
        var writes = Task.Run(async () =>
        {
            gate.Set();
            for (var i = 0; i < 200; i++)
            {
                _ = await store.SaveResultAsync(
                    new DefinitionEvaluationResult(
                        draft.DraftId,
                        1,
                        "fp",
                        "scenario-a",
                        1,
                        "Synthetic",
                        i % 2 == 0,
                        [],
                        recordedAt.AddMilliseconds(i)),
                    CancellationToken.None);
            }
        });
        await Task.WhenAll(reads, writes);
    }

    [Fact]
    public async Task Sqlite_upsert_bumps_revision_and_scenario_atomically()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-eval-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new FileDefinitionResourceContentStore(Path.GetTempPath());
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var store = new SqliteDefinitionDraftEvaluationStore(factory, ids);
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "eval-agent",
                    SampleCandidate("eval-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    DateTimeOffset.Parse("2026-09-25T12:00:00Z")),
                CancellationToken.None);

            var scenario = await store.UpsertScenarioWithRevisionBumpAsync(
                draft.DraftId,
                draft.Revision,
                new DefinitionEvaluationScenarioUpsert(
                    draft.DraftId,
                    "scenario-a",
                    "Title",
                    "Prompt",
                    DefinitionEvaluationRequirementLevel.Required,
                    DefinitionEvaluationCheckType.ToolNotOffered,
                    null,
                    DateTimeOffset.Parse("2026-09-25T12:01:00Z")),
                CancellationToken.None);

            var refreshed = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            Assert.Equal(draft.Revision + 1, refreshed!.Revision);
            var listed = await store.ListScenariosAsync(draft.DraftId, CancellationToken.None);
            Assert.Single(listed);
            Assert.Equal(scenario.ScenarioId, listed[0].ScenarioId);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Sqlite_concurrent_upserts_do_not_corrupt_revision_or_scenarios(int round)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-eval-concurrent-{round}-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new FileDefinitionResourceContentStore(Path.GetTempPath());
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var store = new SqliteDefinitionDraftEvaluationStore(factory, ids);
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "eval-agent",
                    SampleCandidate("eval-agent"),
                    DefinitionDraftSourceKind.New,
                    null,
                    DateTimeOffset.Parse("2026-09-25T12:00:00Z")),
                CancellationToken.None);

            var successes = 0;
            var conflicts = 0;
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                await start.Task;
                try
                {
                    await store.UpsertScenarioWithRevisionBumpAsync(
                        draft.DraftId,
                        draft.Revision,
                        new DefinitionEvaluationScenarioUpsert(
                            draft.DraftId,
                            "scenario-a",
                            $"Title-{i}",
                            "Prompt",
                            DefinitionEvaluationRequirementLevel.Advisory,
                            DefinitionEvaluationCheckType.ToolOffered,
                            "tool",
                            DateTimeOffset.Parse("2026-09-25T12:01:00Z").AddSeconds(i)),
                        CancellationToken.None);
                    Interlocked.Increment(ref successes);
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                    Assert.Equal(409, ex.StatusCode);
                    Interlocked.Increment(ref conflicts);
                }
            })).ToArray();
            start.SetResult();
            await Task.WhenAll(tasks);

            var finalDraft = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            Assert.Equal(1, successes);
            Assert.Equal(19, conflicts);
            Assert.Equal(draft.Revision + successes, finalDraft!.Revision);
            var scenarios = await store.ListScenariosAsync(draft.DraftId, CancellationToken.None);
            Assert.Single(scenarios);
            Assert.Equal(successes, scenarios[0].ScenarioVersion);
            await store.RemoveScenarioWithRevisionBumpAsync(draft.DraftId, finalDraft.Revision, "scenario-a");
            Assert.Empty(await store.ListScenariosAsync(draft.DraftId));
            Assert.Equal(finalDraft.Revision + 1, (await admin.GetDraftAsync(draft.DraftId))!.Revision);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task Sqlite_writer_admission_is_bounded_cancelable_and_released_after_conflict()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-eval-admission-{Guid.NewGuid():N}.db");
        var factory = new SqliteContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, new FileDefinitionResourceContentStore(Path.GetTempPath()));
            var draft = await admin.CreateDraftAsync(new AgentDefinitionDraftCreate("eval-agent", SampleCandidate("eval-agent"),
                DefinitionDraftSourceKind.New, null, DateTimeOffset.UtcNow));
            var blocked = new BlockingContextFactory(factory);
            var store = new SqliteDefinitionDraftEvaluationStore(blocked, ids, busyTimeoutMs: 50);
            var upsert = Scenario(draft.DraftId);
            var first = store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, upsert).AsTask();
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                var conflict = await Assert.ThrowsAsync<AgentCoreException>(() =>
                    store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, upsert).AsTask());
                Assert.Equal("Conflict", conflict.Code);
                Assert.Equal(409, conflict.StatusCode);
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    store.RemoveScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, "scenario-a", canceled.Token).AsTask());
                Assert.Empty(await new SqliteDefinitionDraftEvaluationStore(factory, ids).ListScenariosAsync(draft.DraftId));
                Assert.Equal(draft.Revision, (await admin.GetDraftAsync(draft.DraftId))!.Revision);
            }
            finally { blocked.Release.SetResult(); }
            await first;
            await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, upsert).AsTask());
            await store.RemoveScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision + 1, "scenario-a");
            Assert.Empty(await store.ListScenariosAsync(draft.DraftId));
            Assert.Equal(draft.Revision + 2, (await admin.GetDraftAsync(draft.DraftId))!.Revision);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Sqlite_external_writer_contention_returns_conflict_without_partial_revision_or_scenario()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-eval-busy-{Guid.NewGuid():N}.db");
        var factory = new SqliteContextFactory(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path};Default Timeout=1").Options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, new FileDefinitionResourceContentStore(Path.GetTempPath()));
            var store = new SqliteDefinitionDraftEvaluationStore(factory, ids);
            var draft = await admin.CreateDraftAsync(new AgentDefinitionDraftCreate("eval-agent", SampleCandidate("eval-agent"),
                DefinitionDraftSourceKind.New, null, DateTimeOffset.UtcNow));
            await using var competing = new SqliteConnection($"Data Source={path}");
            await competing.OpenAsync();
            using (var transaction = competing.BeginTransaction())
            {
                var conflict = await Assert.ThrowsAsync<AgentCoreException>(() =>
                    store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, Scenario(draft.DraftId)).AsTask());
                Assert.Equal("Conflict", conflict.Code);
                Assert.Equal(409, conflict.StatusCode);
                var editConflict = await Assert.ThrowsAsync<AgentCoreException>(() => admin.UpdateDraftAsync(
                    new AgentDefinitionDraftUpdate(draft.DraftId, draft.Revision,
                        draft.Candidate with { SystemInstructions = "Contended edit" }, DateTimeOffset.UtcNow)).AsTask());
                Assert.Equal("Conflict", editConflict.Code);
                Assert.Equal(409, editConflict.StatusCode);
                Assert.Equal(draft.Revision, (await admin.GetDraftAsync(draft.DraftId))!.Revision);
                Assert.Equal(draft.Candidate.SystemInstructions, (await admin.GetDraftAsync(draft.DraftId))!.Candidate.SystemInstructions);
                Assert.Empty(await store.ListScenariosAsync(draft.DraftId));
            }
            await store.UpsertScenarioWithRevisionBumpAsync(draft.DraftId, draft.Revision, Scenario(draft.DraftId));
            Assert.Equal(draft.Revision + 1, (await admin.GetDraftAsync(draft.DraftId))!.Revision);
            Assert.Equal(1, Assert.Single(await store.ListScenariosAsync(draft.DraftId)).ScenarioVersion);
        }
        finally { File.Delete(path); }
    }

    private static DefinitionEvaluationScenarioUpsert Scenario(Guid draftId) => new(draftId, "scenario-a", "Title", "Prompt",
        DefinitionEvaluationRequirementLevel.Advisory, DefinitionEvaluationCheckType.ToolOffered, "tool", DateTimeOffset.UtcNow);

    private sealed class BlockingContextFactory(IDbContextFactory<AgentCoreDbContext> inner) : IDbContextFactory<AgentCoreDbContext>
    {
        private int _calls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentCoreDbContext CreateDbContext() => inner.CreateDbContext();
        public async Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.SetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return await inner.CreateDbContextAsync(cancellationToken);
        }
    }

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

        public ValueTask DeleteDraftAsync(
            AgentDefinitionDraftDelete delete,
            CancellationToken cancellationToken = default) =>
            inner.DeleteDraftAsync(delete, cancellationToken);

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

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
