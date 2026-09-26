using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

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

    [Fact]
    public async Task Sqlite_concurrent_upserts_do_not_corrupt_revision_or_scenarios()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-eval-concurrent-{Guid.NewGuid():N}.db");
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

            var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
            {
                var current = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
                try
                {
                    await store.UpsertScenarioWithRevisionBumpAsync(
                        draft.DraftId,
                        current!.Revision,
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
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                }
            }));
            await Task.WhenAll(tasks);

            var finalDraft = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            Assert.True(finalDraft!.Revision > draft.Revision);
            var scenarios = await store.ListScenariosAsync(draft.DraftId, CancellationToken.None);
            Assert.Single(scenarios);
            Assert.True(scenarios[0].ScenarioVersion >= 1);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
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

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}
