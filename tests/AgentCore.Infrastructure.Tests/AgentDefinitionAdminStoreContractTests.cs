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

public sealed class InMemoryAgentDefinitionAdminStoreContractTests : AgentDefinitionAdminStoreContractTests
{
    protected override async Task ForEachStoreAsync(Func<IAgentDefinitionAdminStore, Task> exercise)
    {
        await exercise(new InMemoryAgentDefinitionAdminStore(new SystemIdGenerator(TimeProvider.System)));
    }
}

public sealed class SqliteAgentDefinitionAdminStoreContractTests : AgentDefinitionAdminStoreContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delete_or_publish_removes_draft_resources_and_evaluation_evidence(bool publish)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-def-admin-delete-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var ids = new SystemIdGenerator(TimeProvider.System);
            var content = new InMemoryDefinitionResourceContentStore();
            var admin = new SqliteAgentDefinitionAdminStore(factory, ids, content);
            var resources = new SqliteAgentDefinitionResourceAdminStore(factory, content, ids);
            var evaluations = new SqliteDefinitionDraftEvaluationStore(factory, ids);
            var now = DateTimeOffset.Parse("2026-01-04T00:00:00Z");
            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "delete-with-children",
                    SampleCandidate("delete-with-children"),
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);

            var bytes = System.Text.Encoding.UTF8.GetBytes("draft resource");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            await content.StoreVerifiedAsync(hash, bytes);
            await resources.UpsertDraftResourceAsync(
                new AgentDefinitionDraftResourceUpsert(
                    draft.DraftId,
                    draft.Revision,
                    null,
                    "reference.txt",
                    AgentDefinitionResourceKind.Reference,
                    "text/plain",
                    hash,
                    bytes.Length,
                    now.AddMinutes(1)));

            await evaluations.UpsertScenarioWithRevisionBumpAsync(
                draft.DraftId,
                2,
                new DefinitionEvaluationScenarioUpsert(
                    draft.DraftId,
                    "required-check",
                    "Required check",
                    "Run the required check.",
                    DefinitionEvaluationRequirementLevel.Required,
                    DefinitionEvaluationCheckType.ToolOffered,
                    "workspace.read",
                    now.AddMinutes(2)));
            await evaluations.SaveResultAsync(
                new DefinitionEvaluationResult(
                    draft.DraftId,
                    3,
                    "fingerprint",
                    "required-check",
                    1,
                    "Synthetic",
                    true,
                    [],
                    now.AddMinutes(3)));

            if (publish)
            {
                await Assert.ThrowsAsync<AgentCoreException>(async () => await admin.PublishDraftAsync(
                    new AgentDefinitionDraftPublish(draft.DraftId, 2, [], now.AddMinutes(4), ConsumeDraft: true)));
                Assert.NotNull(await admin.GetDraftAsync(draft.DraftId));
                var publication = await admin.PublishDraftAsync(
                    new AgentDefinitionDraftPublish(draft.DraftId, 3, [], now.AddMinutes(4), ConsumeDraft: true));
                Assert.Single(await resources.ListPublicationResourcesAsync(draft.DefinitionId, publication.Version));
            }
            else
            {
                await admin.DeleteDraftAsync(new AgentDefinitionDraftDelete(draft.DraftId, 3, now.AddMinutes(4)));
            }
            Assert.Null(await admin.GetDraftAsync(draft.DraftId));

            Assert.Empty(await resources.ListDraftResourcesAsync(draft.DraftId));
            Assert.Empty(await evaluations.ListScenariosAsync(draft.DraftId));
            Assert.Empty(await evaluations.ListResultsAsync(draft.DraftId));
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

    [Fact]
    public async Task Reopen_preserves_published_definition()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-def-admin-reopen-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            var store = new SqliteAgentDefinitionAdminStore(
                factory,
                new SystemIdGenerator(TimeProvider.System),
                new InMemoryDefinitionResourceContentStore());
            var now = DateTimeOffset.Parse("2026-01-04T00:00:00Z");
            var candidate = SampleCandidate("demo-agent") with { StateIsolationPolicy = new(MaxAgentStateVisibility: AgentStateVisibility.InstanceSummary) };
            var draft = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            var published = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now.AddMinutes(1), ConsumeDraft: true),
                CancellationToken.None);
            Assert.Null(await store.GetDraftAsync(draft.DraftId));

            var reopened = new SqliteAgentDefinitionAdminStore(
                factory,
                new SystemIdGenerator(TimeProvider.System),
                new InMemoryDefinitionResourceContentStore());
            var loaded = await reopened.GetPublicationAsync("demo-agent", published.Version);
            Assert.NotNull(loaded);
            Assert.Equal(published.Version, loaded!.Version);
            Assert.Equal("You are a demo agent.", loaded.Payload.AgentInstructions);
            Assert.Equal(candidate.StateIsolationPolicy, loaded.Payload.StateIsolationPolicy);
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

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
        new(
            2,
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

    protected override async Task ForEachStoreAsync(Func<IAgentDefinitionAdminStore, Task> exercise)
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-def-admin-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteContextFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await exercise(new SqliteAgentDefinitionAdminStore(
                factory,
                new SystemIdGenerator(TimeProvider.System),
                new InMemoryDefinitionResourceContentStore()));
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

    private sealed class SqliteContextFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CreateDbContext());
    }
}

public abstract class AgentDefinitionAdminStoreContractTests
{
    [Fact]
    public async Task Consuming_publication_removes_only_its_source_draft()
    {
        await ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-04T00:00:00Z");
            var candidate = SampleCandidate("consume-draft");
            var source = await store.CreateDraftAsync(new AgentDefinitionDraftCreate(
                "consume-draft", candidate, DefinitionDraftSourceKind.New, null, now));
            var other = await store.CreateDraftAsync(new AgentDefinitionDraftCreate(
                "consume-draft", candidate, DefinitionDraftSourceKind.New, null, now));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(source.DraftId, source.Revision + 1, [], now, ConsumeDraft: true)));
            Assert.NotNull(await store.GetDraftAsync(source.DraftId));
            var published = await store.PublishDraftAsync(new AgentDefinitionDraftPublish(
                source.DraftId, source.Revision, [], now, ConsumeDraft: true));
            Assert.Null(await store.GetDraftAsync(source.DraftId));
            Assert.NotNull(await store.GetDraftAsync(other.DraftId));
            Assert.NotNull(await store.GetPublicationAsync(source.DefinitionId, published.Version));
        });
    }

    protected abstract Task ForEachStoreAsync(Func<IAgentDefinitionAdminStore, Task> exercise);

    [Fact]
    public Task Draft_delete_is_revision_protected_and_preserves_publications() =>
        ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var candidate = SampleCandidate("delete-demo");
            var draft = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "delete-demo",
                    candidate,
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            var publication = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now.AddMinutes(1)),
                CancellationToken.None);

            await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.DeleteDraftAsync(
                    new AgentDefinitionDraftDelete(draft.DraftId, draft.Revision, now.AddMinutes(2)),
                    CancellationToken.None).AsTask());

            await store.DeleteDraftAsync(
                new AgentDefinitionDraftDelete(draft.DraftId, draft.Revision + 1, now.AddMinutes(2)),
                CancellationToken.None);

            Assert.Null(await store.GetDraftAsync(draft.DraftId));
            Assert.NotNull(await store.GetPublicationAsync("delete-demo", publication.Version));
        });

    [Fact]
    public Task Draft_edit_publish_deprecate_contract() =>
        ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var candidate = SampleCandidate("demo-agent");

            var created = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "demo-agent",
                    candidate,
                    DefinitionDraftSourceKind.New,
                    null,
                    now),
                CancellationToken.None);
            Assert.Equal(1, created.Revision);

            var edited = await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    candidate with { AgentInstructions = "Updated instructions for publish." },
                    now.AddMinutes(1)),
                CancellationToken.None);
            Assert.Equal(2, edited.Revision);

            await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.UpdateDraftAsync(
                    new AgentDefinitionDraftUpdate(
                        created.DraftId,
                        1,
                        candidate,
                        now.AddMinutes(2)),
                    CancellationToken.None).AsTask());

            var publication = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(created.DraftId, 2, [], now.AddMinutes(3)),
                CancellationToken.None);
            Assert.Equal(1, publication.Version);
            Assert.Equal("Updated instructions for publish.", publication.Payload.AgentInstructions);

            var editedAgain = await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    3,
                    candidate with { AgentInstructions = "Second publication body." },
                    now.AddMinutes(4)),
                CancellationToken.None);
            var secondPublication = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(editedAgain.DraftId, editedAgain.Revision, [1], now.AddMinutes(5)),
                CancellationToken.None);
            Assert.Equal(2, secondPublication.Version);

            var deprecated = await store.DeprecatePublicationAsync(
                new AgentDefinitionPublicationDeprecate("demo-agent", 1, 1, now.AddMinutes(6)),
                CancellationToken.None);
            Assert.Equal(DefinitionPublicationStatus.Deprecated, deprecated.Status);
            Assert.NotNull(await store.GetPublicationAsync("demo-agent", 1));
        });

    [Fact]
    public Task Published_payload_survives_caller_mutation_of_source_collections() =>
        ForEachStoreAsync(async store =>
        {
            var goals = new List<string> { "Initial goal" };
            var metadata = new Dictionary<string, string> { ["tag"] = "alpha" };
            var candidate = SampleCandidate("demo-agent") with { Goals = goals, Metadata = metadata };
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var created = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            goals.Add("Injected after create");
            metadata["tag"] = "mutated";

            var publishCandidate = SampleCandidate("demo-agent") with
            {
                AgentInstructions = "Publish body",
                Goals = new[] { "Initial goal" },
                Metadata = new Dictionary<string, string> { ["tag"] = "alpha" }
            };
            var edited = await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    publishCandidate,
                    now.AddMinutes(1)),
                CancellationToken.None);
            var published = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(edited.DraftId, edited.Revision, [], now.AddMinutes(2)),
                CancellationToken.None);

            goals.Clear();
            metadata["tag"] = "cleared";
            var reloaded = await store.GetPublicationAsync("demo-agent", published.Version);
            Assert.NotNull(reloaded);
            Assert.Equal("Initial goal", Assert.Single(reloaded!.Payload.Goals));
            Assert.Equal("alpha", reloaded.Payload.Metadata["tag"]);
            Assert.Equal("Publish body", reloaded.Payload.AgentInstructions);

            var draftView = await store.GetDraftAsync(created.DraftId);
            Assert.NotNull(draftView);
            if (draftView!.Candidate.Goals is IList<string> mutableGoals)
            {
                mutableGoals.Add("mutate returned draft");
            }

            var draftAgain = await store.GetDraftAsync(created.DraftId);
            Assert.Equal("Initial goal", Assert.Single(draftAgain!.Candidate.Goals));
        });

    [Fact]
    public Task Concurrent_publish_and_update_from_same_revision_commit_once() =>
        ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var candidate = SampleCandidate("demo-agent");
            var created = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    candidate with { AgentInstructions = "Ready to race" },
                    now.AddMinutes(1)),
                CancellationToken.None);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var publishAttempt = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await store.PublishDraftAsync(
                        new AgentDefinitionDraftPublish(created.DraftId, 2, [], now.AddMinutes(2)),
                        CancellationToken.None);
                    return true;
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                    return false;
                }
            });
            var updateAttempt = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await store.UpdateDraftAsync(
                        new AgentDefinitionDraftUpdate(
                            created.DraftId,
                            2,
                            candidate with { AgentInstructions = "Concurrent edit" },
                            now.AddMinutes(3)),
                        CancellationToken.None);
                    return true;
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                    return false;
                }
            });
            gate.SetResult();
            var outcomes = await Task.WhenAll(publishAttempt, updateAttempt);
            Assert.Equal(1, outcomes.Count(success => success));

            var publications = await store.ListPublicationsAsync("demo-agent");
            Assert.True(publications.Count is 0 or 1);
            var draft = await store.GetDraftAsync(created.DraftId);
            Assert.NotNull(draft);
            Assert.Equal(3, draft!.Revision);
        });

    [Fact]
    public Task Concurrent_delete_and_update_from_same_revision_commit_once() =>
        ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var candidate = SampleCandidate("demo-agent");
            var created = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    candidate with { AgentInstructions = "Rev 2" },
                    now.AddMinutes(1)),
                CancellationToken.None);
            await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    2,
                    candidate with { AgentInstructions = "Rev 3" },
                    now.AddMinutes(2)),
                CancellationToken.None);

            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deleteAttempt = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await store.DeleteDraftAsync(
                        new AgentDefinitionDraftDelete(created.DraftId, 3, now.AddMinutes(3)),
                        CancellationToken.None);
                    return true;
                }
                catch (AgentCoreException ex) when (ex.Code == "Conflict")
                {
                    return false;
                }
            });
            var updateAttempt = Task.Run(async () =>
            {
                await gate.Task;
                try
                {
                    await store.UpdateDraftAsync(
                        new AgentDefinitionDraftUpdate(
                            created.DraftId,
                            3,
                            candidate with { AgentInstructions = "Concurrent edit" },
                            now.AddMinutes(4)),
                        CancellationToken.None);
                    return true;
                }
                catch (AgentCoreException ex) when (ex.Code is "Conflict" or "NotFound")
                {
                    return false;
                }
            });
            gate.SetResult();
            var outcomes = await Task.WhenAll(deleteAttempt, updateAttempt);
            Assert.Equal(1, outcomes.Count(success => success));

            var draft = await store.GetDraftAsync(created.DraftId);
            if (outcomes[0])
            {
                Assert.Null(draft);
            }
            else
            {
                Assert.NotNull(draft);
                Assert.Equal("Concurrent edit", draft!.Candidate.AgentInstructions);
                Assert.Equal(4, draft.Revision);
            }
        });

    [Fact]
    public Task Duplicate_publish_from_stale_revision_conflicts() =>
        ForEachStoreAsync(async store =>
        {
            var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var candidate = SampleCandidate("demo-agent");
            var created = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    1,
                    candidate with { AgentInstructions = "v1" },
                    now.AddMinutes(1)),
                CancellationToken.None);
            await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(created.DraftId, 2, [], now.AddMinutes(2)),
                CancellationToken.None);
            await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.PublishDraftAsync(
                    new AgentDefinitionDraftPublish(created.DraftId, 2, [1], now.AddMinutes(3)),
                    CancellationToken.None).AsTask());
        });

    private static AgentDefinitionCandidate SampleCandidate(string definitionId) =>
        new(
            2,
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
