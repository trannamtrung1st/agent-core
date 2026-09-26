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
    [Fact]
    public async Task Delete_removes_draft_resources_and_evaluation_evidence()
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

            await admin.DeleteDraftAsync(
                new AgentDefinitionDraftDelete(draft.DraftId, 3, now.AddMinutes(4)));

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
            var candidate = SampleCandidate("demo-agent");
            var draft = await store.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            var published = await store.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now.AddMinutes(1)),
                CancellationToken.None);

            var reopened = new SqliteAgentDefinitionAdminStore(
                factory,
                new SystemIdGenerator(TimeProvider.System),
                new InMemoryDefinitionResourceContentStore());
            var loaded = await reopened.GetPublicationAsync("demo-agent", published.Version);
            Assert.NotNull(loaded);
            Assert.Equal(published.Version, loaded!.Version);
            Assert.Equal("You are a demo agent.", loaded.Payload.SystemInstructions);
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
                    candidate with { SystemInstructions = "Updated instructions for publish." },
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
            Assert.Equal("Updated instructions for publish.", publication.Payload.SystemInstructions);

            var editedAgain = await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    3,
                    candidate with { SystemInstructions = "Second publication body." },
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
                SystemInstructions = "Publish body",
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
            Assert.Equal("Publish body", reloaded.Payload.SystemInstructions);

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
                    candidate with { SystemInstructions = "Ready to race" },
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
                            candidate with { SystemInstructions = "Concurrent edit" },
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
                    candidate with { SystemInstructions = "Rev 2" },
                    now.AddMinutes(1)),
                CancellationToken.None);
            await store.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    created.DraftId,
                    2,
                    candidate with { SystemInstructions = "Rev 3" },
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
                            candidate with { SystemInstructions = "Concurrent edit" },
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
                Assert.Equal("Concurrent edit", draft!.Candidate.SystemInstructions);
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
                    candidate with { SystemInstructions = "v1" },
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

    [Fact]
    public Task Composite_list_inventory_resolves_default_not_deprecated_highest_version() =>
        ForEachStoreAsync(async admin =>
        {
            var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
            var composite = new CompositeAgentDefinitionStore(builtIns, admin);
            var now = DateTimeOffset.Parse("2026-01-03T00:00:00Z");
            var candidate = SampleCandidate("inventory-agent");

            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate("inventory-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            var v1 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now.AddMinutes(1)),
                CancellationToken.None);
            var v2Draft = await admin.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    draft.DraftId,
                    2,
                    candidate with { SystemInstructions = "Active v2 body." },
                    now.AddMinutes(2)),
                CancellationToken.None);
            var v2 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(v2Draft.DraftId, v2Draft.Revision, [v1.Version], now.AddMinutes(3)),
                CancellationToken.None);
            var draftAfterV2 = await admin.GetDraftAsync(draft.DraftId, CancellationToken.None);
            var v3Draft = await admin.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    draft.DraftId,
                    draftAfterV2!.Revision,
                    candidate with { SystemInstructions = "Deprecated v3 body." },
                    now.AddMinutes(4)),
                CancellationToken.None);
            var v3 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(v3Draft.DraftId, v3Draft.Revision, [v2.Version], now.AddMinutes(5)),
                CancellationToken.None);
            await admin.DeprecatePublicationAsync(
                new AgentDefinitionPublicationDeprecate("inventory-agent", v3.Version, 1, now.AddMinutes(6)),
                CancellationToken.None);

            var all = await composite.ListAsync(CancellationToken.None);
            var ids = all.Select(item => item.Id).Distinct(StringComparer.Ordinal);
            var inventory = new List<AgentDefinition>();
            foreach (var id in ids)
            {
                var resolved = await composite.GetAsync(id, version: null, CancellationToken.None);
                if (resolved is not null)
                {
                    inventory.Add(resolved);
                }
            }

            var listed = inventory.Single(item => item.Id == "inventory-agent");
            Assert.Equal(v2.Version, listed.Version);
            Assert.Equal("Active v2 body.", listed.SystemInstructions);
            var exactDeprecated = await composite.GetAsync("inventory-agent", v3.Version, CancellationToken.None);
            Assert.NotNull(exactDeprecated);
            Assert.Equal("Deprecated v3 body.", exactDeprecated!.SystemInstructions);
        });

    [Fact]
    public Task Composite_default_lookup_skips_deprecated_publication() =>
        ForEachStoreAsync(async admin =>
        {
            var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
            var composite = new CompositeAgentDefinitionStore(builtIns, admin);
            var now = DateTimeOffset.Parse("2026-01-03T00:00:00Z");
            var candidate = SampleCandidate("demo-agent");

            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate("demo-agent", candidate, DefinitionDraftSourceKind.New, null, now),
                CancellationToken.None);
            var publishedV1 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(draft.DraftId, draft.Revision, [], now.AddMinutes(1)),
                CancellationToken.None);
            await admin.DeprecatePublicationAsync(
                new AgentDefinitionPublicationDeprecate("demo-agent", publishedV1.Version, 1, now.AddMinutes(2)),
                CancellationToken.None);

            var edited = await admin.UpdateDraftAsync(
                new AgentDefinitionDraftUpdate(
                    draft.DraftId,
                    2,
                    candidate with { SystemInstructions = "Active v2 body." },
                    now.AddMinutes(3)),
                CancellationToken.None);
            var publishedV2 = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(edited.DraftId, edited.Revision, [publishedV1.Version], now.AddMinutes(4)),
                CancellationToken.None);

            var exactDeprecated = await composite.GetAsync("demo-agent", publishedV1.Version);
            var defaultResolved = await composite.GetAsync("demo-agent");
            Assert.NotNull(exactDeprecated);
            Assert.NotNull(defaultResolved);
            Assert.Equal("You are a demo agent.", exactDeprecated!.SystemInstructions);
            Assert.Equal("Active v2 body.", defaultResolved!.SystemInstructions);
            Assert.Equal(publishedV2.Version, defaultResolved.Version);
        });

    [Fact]
    public Task Composite_store_resolves_durable_publication_alongside_built_ins() =>
        ForEachStoreAsync(async admin =>
        {
            var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
            var composite = new CompositeAgentDefinitionStore(builtIns, admin);
            var examiner = await builtIns.GetAsync("examiner", 1);
            Assert.NotNull(examiner);

            var occupied = (await builtIns.ListAsync())
                .Where(item => string.Equals(item.Id, "examiner", StringComparison.Ordinal))
                .Select(item => item.Version)
                .ToArray();

            var draft = await admin.CreateDraftAsync(
                new AgentDefinitionDraftCreate(
                    "examiner",
                    AgentDefinitionCandidate.FromDefinition(examiner!) with
                    {
                        SystemInstructions = examiner!.SystemInstructions + "\nDurable overlay."
                    },
                    DefinitionDraftSourceKind.ForkBuiltIn,
                    1,
                    DateTimeOffset.Parse("2026-01-02T00:00:00Z")),
                CancellationToken.None);

            var published = await admin.PublishDraftAsync(
                new AgentDefinitionDraftPublish(
                    draft.DraftId,
                    draft.Revision,
                    occupied,
                    DateTimeOffset.Parse("2026-01-02T00:01:00Z")),
                CancellationToken.None);
            Assert.Equal(occupied.Max() + 1, published.Version);

            var exactBuiltin = await composite.GetAsync("examiner", 1);
            var exactDurable = await composite.GetAsync("examiner", published.Version);
            Assert.NotNull(exactBuiltin);
            Assert.NotNull(exactDurable);
            Assert.DoesNotContain("Durable overlay.", exactBuiltin!.SystemInstructions, StringComparison.Ordinal);
            Assert.Contains("Durable overlay.", exactDurable!.SystemInstructions, StringComparison.Ordinal);
        });

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

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
