using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class StructuredMemoryContractTests
{
    private static readonly Guid SessionA = Guid.Parse("019944af-0008-7000-8000-0000000000a1");
    private static readonly Guid SessionB = Guid.Parse("019944af-0008-7000-8000-0000000000b1");

    [Fact]
    public async Task Both_stores_create_retrieve_and_bound_five_kinds()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time, 16);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            foreach (var kind in new[] { MemoryKind.Fact, MemoryKind.Preference, MemoryKind.Goal, MemoryKind.Decision, MemoryKind.OpenLoop })
            {
                await service.WriteAsync(owner, new MemoryWriteProposal(kind, $"{kind} subject", $"{kind} body", []), admission);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            var all = await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(5, all.Count);
            Assert.Equal(MemoryKind.OpenLoop, all[0].Kind);
            var goal = Assert.Single(await service.SearchAsync(owner, new MemorySearchQuery("goal body", MemoryKind.Goal), admission));
            Assert.Equal("Goal body", goal.Content);
            Assert.NotNull(await service.GetAsync(owner, goal.MemoryId, admission));
        });
    }

    [Fact]
    public async Task Scope_reset_tombstones_all_active_session_items_in_one_operation()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time, 8);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            for (var index = 0; index < 3; index++)
            {
                await service.WriteAsync(
                    owner,
                    new MemoryWriteProposal(MemoryKind.Fact, $"Subject {index}", $"Body {index}", []),
                    admission);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(3, await store.CountActiveAsync(SessionA));
            var removed = await service.ResetSessionScopeAsync(owner);
            Assert.Equal(3, removed);
            Assert.Equal(0, await store.CountActiveAsync(SessionA));
            Assert.Empty(await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission));
        });
    }

    [Fact]
    public async Task Correction_supersedes_the_named_item_and_keeps_provenance()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time, 8);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            var entry = Guid.Parse("019944af-0008-7000-8000-0000000000e1");
            var created = await service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Fact, "Ship the report", "by Friday", [entry]),
                admission);
            time.Advance(TimeSpan.FromSeconds(1));
            var conflict = await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Fact, "ship   the report", "next week", []),
                admission).AsTask());
            Assert.Equal("Conflict", conflict.Code);

            var corrected = await service.UpdateAsync(
                owner,
                new MemoryUpdateProposal(created.MemoryId, "Ship the report", "by Monday", [entry]),
                admission);
            Assert.Equal(created.MemoryId, corrected.Provenance.SupersedesMemoryId);
            Assert.Equal([entry], corrected.Provenance.SourceEntryIds);
            Assert.Equal(MemoryKind.Fact, corrected.Kind);
            var visible = Assert.Single(await service.SearchAsync(owner, new MemorySearchQuery("report", null), admission));
            Assert.Equal("by Monday", visible.Content);
            var prior = await service.GetAsync(owner, created.MemoryId, admission);
            Assert.Equal(MemoryItemStatus.Superseded, prior!.Status);
            Assert.Equal("by Friday", prior.Content);
        });
    }

    [Fact]
    public async Task Deletion_leaves_a_content_free_tombstone()
    {
        await ForEachStore(async store =>
        {
            var service = Service(store, Clock(), 4);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            var created = await service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Decision, "Use SQLite", "for the demo", []),
                admission);
            var tombstone = await service.DeleteAsync(owner, created.MemoryId);
            Assert.Equal(MemoryItemStatus.Deleted, tombstone.Status);
            Assert.Equal(string.Empty, tombstone.Subject);
            Assert.Equal(string.Empty, tombstone.Content);
            Assert.Equal("application", tombstone.Provenance.Source);
            Assert.Empty(await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission));
            var loaded = await service.GetAsync(owner, created.MemoryId, admission);
            Assert.Equal(string.Empty, loaded!.Content);
        });
    }

    [Fact]
    public async Task Another_session_cannot_read_or_mutate_owned_memory()
    {
        await ForEachStore(async store =>
        {
            var service = Service(store, Clock(), 4);
            var owner = new TrustedMemoryOwner(SessionA);
            var other = new TrustedMemoryOwner(SessionB);
            var admission = Admission();
            var created = await service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.Fact, "Session secret name", "only A", []),
                admission);
            Assert.Empty(await service.SearchAsync(other, new MemorySearchQuery("session", null), admission));
            Assert.Null(await service.GetAsync(other, created.MemoryId, admission));
            var update = await Assert.ThrowsAsync<AgentCoreException>(() => service.UpdateAsync(
                other,
                new MemoryUpdateProposal(created.MemoryId, "Session secret name", "stolen", []),
                admission).AsTask());
            var delete = await Assert.ThrowsAsync<AgentCoreException>(() => service.DeleteAsync(other, created.MemoryId).AsTask());
            Assert.Equal("NotFound", update.Code);
            Assert.Equal("NotFound", delete.Code);
            Assert.Equal("only A", (await service.GetAsync(owner, created.MemoryId, admission))!.Content);
        });
    }

    [Fact]
    public async Task Admission_rejects_secrets_tails_and_trusted_field_subjects()
    {
        await ForEachStore(async store =>
        {
            var service = Service(store, Clock(), 8);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission("UNHEARD_TAIL_SENTINEL", "provider-reasoning-sentinel");
            await Rejects(service, owner, admission, "token", "sk-abcdefghijklmnopqrstuvwxyz");
            await Rejects(service, owner, admission, "key", "-----BEGIN PRIVATE KEY-----");
            await Rejects(service, owner, admission, "blob", new string('A', 80));
            await Rejects(service, owner, admission, "tail", "keep UNHEARD_TAIL_SENTINEL hidden");
            await Rejects(service, owner, admission, "reasoning", "provider-reasoning-sentinel");
            await Rejects(service, owner, admission, "preferredName", "Pat");
            await Rejects(service, owner, admission, "Tone", "warm");
            Assert.Empty(await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission));
        });
    }

    [Fact]
    public async Task Occupied_trusted_subjects_stay_out_of_retrieval()
    {
        await ForEachStore(async store =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
            var item = new StructuredMemoryItem(
                Guid.Parse("019944af-0008-7000-8000-0000000000c1"),
                SessionA,
                MemoryKind.Preference,
                MemoryItemStatus.Active,
                "preferredName",
                "Pat",
                "preferredname",
                new MemoryProvenance("legacy", [], null, now),
                now,
                now);
            await store.InsertAsync(item);
            var service = Service(store, Clock(), 2);
            var owner = new TrustedMemoryOwner(SessionA);
            var occupied = Admission(occupied: ["preferredName"]);
            Assert.Empty(await service.SearchAsync(owner, new MemorySearchQuery("Pat", null), occupied));
            Assert.Null(await service.GetAsync(owner, item.MemoryId, occupied));
            Assert.Single(await service.SearchAsync(owner, new MemorySearchQuery("Pat", null), Admission()));
        });
    }

    [Fact]
    public async Task Search_stops_at_the_item_and_character_bounds()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time, 40);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            for (var index = 0; index < 21; index++)
            {
                await service.WriteAsync(
                    owner,
                    new MemoryWriteProposal(MemoryKind.Fact, $"item {index}", "note", []),
                    admission);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            var page = await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(MemoryLimits.SearchMaxItems, page.Count);
            Assert.DoesNotContain(page, item => item.Subject == "item 0");
            Assert.Contains(page, item => item.Subject == "item 20");
        });
    }

    [Fact]
    public async Task Search_character_budget_limits_long_items()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time, 8);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            var body = string.Create(2000, 0, static (span, _) =>
            {
                for (var index = 0; index < span.Length; index++)
                {
                    span[index] = index % 20 == 19 ? ' ' : 'n';
                }
            });
            for (var index = 0; index < 4; index++)
            {
                await service.WriteAsync(
                    owner,
                    new MemoryWriteProposal(MemoryKind.Fact, $"long {index}", body, []),
                    admission);
                time.Advance(TimeSpan.FromSeconds(1));
            }

            var page = await service.SearchAsync(owner, new MemorySearchQuery(null, null), admission);
            Assert.Equal(2, page.Count);
        });
    }

    [Fact]
    public async Task Active_capacity_rejects_the_next_write()
    {
        await ForEachStore(async store =>
        {
            var service = Service(store, Clock(), MemoryLimits.MaxActiveItems + 2);
            var owner = new TrustedMemoryOwner(SessionA);
            var admission = Admission();
            for (var index = 0; index < MemoryLimits.MaxActiveItems; index++)
            {
                await service.WriteAsync(
                    owner,
                    new MemoryWriteProposal(MemoryKind.OpenLoop, $"loop {index}", "open", []),
                    admission);
            }

            var rejected = await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(
                owner,
                new MemoryWriteProposal(MemoryKind.OpenLoop, "loop overflow", "open", []),
                admission).AsTask());
            Assert.Equal("MemoryCapacity", rejected.Code);
            Assert.Equal(MemoryLimits.MaxActiveItems, await store.CountActiveAsync(SessionA));
        });
    }

    [Fact]
    public async Task Learned_memory_does_not_change_the_trusted_profile()
    {
        await ForEachPair(async (sessions, memories) =>
        {
            var now = new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
            var profile = new UserProfile(
                LocalUserProfile.Id,
                1,
                new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
                {
                    ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                    ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
                },
                now);
            await sessions.SaveProfileAsync(profile, 0);
            var service = Service(memories, Clock(), 4);
            await service.WriteAsync(
                new TrustedMemoryOwner(SessionA),
                new MemoryWriteProposal(MemoryKind.Preference, "Call window", "mornings", []),
                Admission());
            var loaded = await sessions.LoadProfileAsync(LocalUserProfile.Id);
            Assert.Equal(1, loaded!.Revision);
            Assert.Equal("Pat", loaded.Preferences["preferredName"].Value);
            Assert.Equal(UserProfileValueSource.UserSet, loaded.Preferences["preferredName"].Source);
        });
    }

    [Fact]
    public async Task Sqlite_reopen_and_session_delete_keep_ownership()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-memory-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        var sessions = new SqliteMemoryStore(factory, Clock());
        await sessions.EnsureCreatedAsync();
        try
        {
            var first = new SqliteStructuredMemoryStore(factory);
            var service = Service(first, Clock(), 4);
            var created = await service.WriteAsync(
                new TrustedMemoryOwner(SessionA),
                new MemoryWriteProposal(MemoryKind.Goal, "Remember the gate", "P4B", []),
                Admission());
            await service.WriteAsync(
                new TrustedMemoryOwner(SessionB),
                new MemoryWriteProposal(MemoryKind.Goal, "Other session", "leave", []),
                Admission());
            var reopened = new SqliteStructuredMemoryStore(factory);
            var restored = Assert.Single(await Service(reopened, Clock(), 2).SearchAsync(
                new TrustedMemoryOwner(SessionA),
                new MemorySearchQuery("gate", null),
                Admission()));
            Assert.Equal(created.MemoryId, restored.MemoryId);
            await reopened.DeleteSessionAsync(SessionA);
            Assert.Empty(await Service(reopened, Clock(), 2).SearchAsync(
                new TrustedMemoryOwner(SessionA),
                new MemorySearchQuery(null, null),
                Admission()));
            Assert.Single(await Service(reopened, Clock(), 2).SearchAsync(
                new TrustedMemoryOwner(SessionB),
                new MemorySearchQuery(null, null),
                Admission()));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private static async Task Rejects(
        StructuredMemoryService service,
        TrustedMemoryOwner owner,
        MemoryAdmissionContext admission,
        string subject,
        string content)
    {
        var rejected = await Assert.ThrowsAsync<AgentCoreException>(() => service.WriteAsync(
            owner,
            new MemoryWriteProposal(MemoryKind.Fact, subject, content, []),
            admission).AsTask());
        Assert.Equal("MemoryRejected", rejected.Code);
        Assert.DoesNotContain(content, rejected.Message, StringComparison.Ordinal);
    }

    private static MemoryAdmissionContext Admission(params string[] forbidden) =>
        Admission(forbidden, []);

    private static MemoryAdmissionContext Admission(IReadOnlyList<string> forbidden, IReadOnlyList<string> occupied) =>
        new("application", forbidden, occupied.ToHashSet(StringComparer.Ordinal));

    private static MemoryAdmissionContext Admission(IReadOnlyList<string> occupied) =>
        Admission([], occupied);

    private static StructuredMemoryService Service(IStructuredMemoryStore store, TimeProvider time, int ids) =>
        new(store, new DeterministicIdGenerator(
            Enumerable.Range(1, ids).Select(index => Guid.Parse($"019944af-0010-7000-8000-{index:D12}")),
            [Guid.Parse("019944af-0010-7000-8000-0000000000ff")]), time);

    private static FakeTimeProvider Clock() =>
        new(new DateTimeOffset(2026, 9, 23, 4, 0, 0, TimeSpan.Zero));

    private static async Task ForEachStore(Func<IStructuredMemoryStore, Task> exercise)
    {
        await exercise(new InMemoryStructuredMemoryStore());
        await ForEachPair(async (_, memories) => await exercise(memories));
    }

    private static async Task ForEachPair(Func<IMemoryStore, IStructuredMemoryStore, Task> exercise)
    {
        await exercise(new InMemoryMemoryStore(), new InMemoryStructuredMemoryStore());
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-memory-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new TestFactory(options);
        var sessions = new SqliteMemoryStore(factory, Clock());
        await sessions.EnsureCreatedAsync();
        try
        {
            await exercise(sessions, new SqliteStructuredMemoryStore(factory));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
