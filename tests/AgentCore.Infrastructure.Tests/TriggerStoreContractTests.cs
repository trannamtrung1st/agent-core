using System.Data;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class TriggerStoreContractTests
{
    private static readonly Guid InstanceA = Guid.Parse("019944af-0006-7000-8000-0000000000a1");
    private static readonly Guid ProfileA = Guid.Parse("019944af-0006-7000-8000-0000000000b1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-0006-7000-8000-0000000000a2");
    private static readonly Guid ProfileB = Guid.Parse("019944af-0006-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 4, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceSessionId = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");

    [Fact]
    public async Task Admin_capacity_matches_chat_count_excluding_event_subscriptions_on_create_and_enable()
    {
        await ForEachStore(async store =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            Automation Row(Guid id, AutomationStatus status, long revision = 1, Guid? source = null) =>
                new(id, owner, status, "Known task", source is Guid sourceId ? new EventTrigger(sourceId) : new ScheduleTrigger(OneShot()), source is null && status == AutomationStatus.Active ? Now.AddDays(1) : null,
                    null, 0, revision, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, Now, Now), null);
            AgentCore.Application.Admin.AdminEventAppend Audit() => new(Guid.NewGuid(), Now,
                AgentCore.Application.Admin.AdminEventActorKind.LocalOwner, AgentCore.Application.Admin.AdminEventOperationKind.AutomationChanged,
                "agentInstance", InstanceA.ToString("D"), null, null,
                System.Text.Json.JsonSerializer.Serialize(new { instanceId = InstanceA.ToString("D"), operation = "update" }));
            for (var i = 0; i < 3; i++) await store.CreateAsync(Row(Guid.NewGuid(), AutomationStatus.Active, source: Guid.NewGuid()));
            Assert.Equal(3, await store.CountActiveAsync(owner));
            var firstId = Guid.NewGuid(); var secondId = Guid.NewGuid();
            await store.SaveAutomationAsync(Row(firstId, AutomationStatus.Active), 0, Audit(), maxActiveRegistrations: 4);
            Assert.Equal(4, await store.CountActiveAsync(owner));
            await Assert.ThrowsAsync<AgentCoreException>(() => store.SaveAutomationAsync(Row(secondId, AutomationStatus.Active), 0,
                Audit(), maxActiveRegistrations: 4).AsTask());
            Assert.Null(await store.GetAsync(owner, secondId));
            await store.SaveAutomationAsync(Row(firstId, AutomationStatus.Disabled, 2), 1, Audit(), maxActiveRegistrations: 4);
            Assert.Equal(3, await store.CountActiveAsync(owner));
            await store.SaveAutomationAsync(Row(secondId, AutomationStatus.Active), 0, Audit(), maxActiveRegistrations: 4);
            await Assert.ThrowsAsync<AgentCoreException>(() => store.SaveAutomationAsync(Row(firstId, AutomationStatus.Active, 3), 2,
                Audit(), maxActiveRegistrations: 4).AsTask());
            Assert.Equal(AutomationStatus.Disabled, (await store.GetAsync(owner, firstId))!.Status);
            Assert.Equal(4, await store.CountActiveAsync(owner));
        });
    }

    [Fact]
    public async Task Schedule_pages_keep_stable_tie_order_and_owner_isolation()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var owner = new TriggerOwner(InstanceA, ProfileA);
            for (var index = 0; index < 45; index++)
                await Seed(store, owner, $"Reminder {index}", OneShot(), time.GetUtcNow());
            var seen = new List<Guid>();
            Guid? before = null;
            while (true)
            {
                var page = await store.ListAutomationsPageAsync(owner, 20, before);
                if (page.Count == 0) break;
                seen.AddRange(page.Select(item => item.AutomationId));
                before = page[^1].AutomationId;
            }
            Assert.Equal(45, seen.Count);
            Assert.Equal(45, seen.Distinct().Count());
            var all = await store.ListAsync(owner, null);
            Assert.Equal(all.Select(item => item.AutomationId), seen);
            var foreign = new TriggerOwner(InstanceB, ProfileB);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => store.ListAutomationsPageAsync(foreign, 20, before).AsTask());
            Assert.Equal("NotFound", error.Code);
        });
    }

    [Fact]
    public async Task ListAsync_returns_newest_created_first()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var a = await Seed(store, owner, "A", OneShot(), time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(1));
            var b = await Seed(store, owner, "B", OneShot(), time.GetUtcNow());
            time.Advance(TimeSpan.FromSeconds(1));
            var c = await Seed(store, owner, "C", OneShot(), time.GetUtcNow());

            var listed = await store.ListAsync(owner, null);
            Assert.Equal([c.AutomationId, b.AutomationId, a.AutomationId], listed.Select(row => row.AutomationId).ToArray());
        });
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_registration_and_occurrence()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        var time = Clock();
        try
        {
            await new SqliteMemoryStore(factory, time).EnsureCreatedAsync();
            var store = new SqliteTriggerStore(factory);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await Seed(store, owner, "Call John", OneShot(), time.GetUtcNow());
            var admitted = await store.AdmitOccurrenceAsync(new TriggerOccurrence(Guid.NewGuid(),
                "registration|1|1758607200000", created.AutomationId, owner, TriggerSourceKind.Schedule,
                created.NextOccurrenceAtUtc, Now, Now, "{}", null, created.TriggerRevision,
                OccurrenceRoutingDisposition.Pending, null, 0, null, null, null));

            var reopened = new SqliteTriggerStore(factory);
            var loaded = await reopened.GetAsync(owner, created.AutomationId);
            Assert.NotNull(loaded);
            Assert.Equal("Call John", loaded!.Instructions);
            Assert.True(OneShot().SemanticEquals(loaded.Schedule));
            Assert.Equal("Asia/Ho_Chi_Minh", ((OneShotSchedule)loaded.Schedule).TimeZoneId);
            Assert.Equal(new DateOnly(2026, 9, 24), ((OneShotSchedule)loaded.Schedule).LocalDate);
            Assert.Equal(new TimeOnly(9, 0), ((OneShotSchedule)loaded.Schedule).LocalTime);
            var occurrence = await reopened.GetOccurrenceAsync(owner, admitted.Occurrence.OccurrenceId);
            Assert.Equal(admitted.Occurrence.DedupeKey, occurrence!.DedupeKey);
            Assert.Equal(created.AutomationId, occurrence.AutomationId);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Canonical_schema_keeps_triggers_after_session_and_memory_deletion()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        var time = Clock();
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
            }

            await new SqliteMemoryStore(factory, time).EnsureCreatedAsync();
            await new SqliteMemoryStore(factory, time).SaveAsync(Snapshot(SourceSessionId), 0);
            await using (var db = await factory.CreateDbContextAsync())
            {
                var tables = await TableNamesAsync(db);
                Assert.Contains("Automations", tables);
                Assert.Contains("TriggerOccurrences", tables);
                Assert.DoesNotContain(tables, name => name.Contains("Timer", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(8, await ForeignKeyCountAsync(db, "Automations"));
                Assert.Equal(new[] { "AgentInstanceId", "ExecutorPrincipalId", "PinnedEnvironmentId", "PinnedSpaceId", "PinnedSpaceId", "TenantId", "TenantId", "TenantId" }, await ForeignKeyColumnsAsync(db, "Automations"));
                Assert.Equal(new[] { "AcceptedAgentRunId", "AcceptedWorkflowExecutionId", "AgentInstanceId", "ExecutionSessionId", "ExecutorPrincipalId", "LiveSessionId", "PinnedEnvironmentId", "PinnedSpaceId", "PinnedSpaceId", "TenantId", "TenantId", "TenantId", "TenantId" },
                    await ForeignKeyColumnsAsync(db, "TriggerOccurrences"));
            }

            var owner = new TriggerOwner(InstanceA, ProfileA);
            var store = new SqliteTriggerStore(factory);
            var created = await Seed(store, owner, "Call John", OneShot(), time.GetUtcNow());
            await new SqliteStructuredMemoryStore(factory).InsertAsync(new StructuredMemoryItem(
                Guid.Parse("019944af-0006-7000-8000-0000000000ee"),
                SourceSessionId,
                MemoryKind.Fact,
                MemoryItemStatus.Active,
                "Call window",
                "mornings",
                "call window",
                new MemoryProvenance("application", [], null, Now),
                Now,
                Now));
            await new SqliteStructuredMemoryStore(factory).DeleteSessionAsync(SourceSessionId);
            await using (var db = await factory.CreateDbContextAsync())
            {
                Assert.Equal(0, await db.StructuredMemories.CountAsync());
                var session = await db.Sessions.SingleAsync();
                db.Sessions.Remove(session);
                await db.SaveChangesAsync();
                Assert.Equal(0, await db.Sessions.CountAsync());
            }

            var loaded = await new SqliteTriggerStore(factory).GetAsync(owner, created.AutomationId);
            Assert.Equal("Call John", loaded!.Instructions);
            Assert.Equal(SourceSessionId, loaded.Provenance.SourceSessionId);
            Assert.Equal(owner, loaded.Owner);
            Assert.Equal(AutomationStatus.Active, loaded.Status);
        }
        finally
        {
            Release(path);
        }
    }

    // Literal retained records for physical storage contracts, not Automation admission or IAM.
    private static ValueTask<Automation> Seed(ITriggerStore store, TriggerOwner owner, string instructions, TriggerSchedule schedule, DateTimeOffset now) =>
        store.CreateAsync(new Automation(Guid.NewGuid(), owner, AutomationStatus.Active, instructions,
            new ScheduleTrigger(schedule), schedule is OneShotSchedule one ? one.AtUtc : null, null, 0, 1, 1,
            new(TriggerAuthorizationOrigin.CurrentUserTurn, SourceSessionId,
                Guid.Parse("019944af-0006-7000-8000-0000000000e9"), now, now), null));

    private static OneShotSchedule OneShot() =>
        new(Now.AddDays(1), "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 24), new TimeOnly(9, 0));

    private static WeeklySchedule Weekly() =>
        new(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London");

    private static FakeTimeProvider Clock() => new(Now);

    private static async Task ForEachStore(Func<ITriggerStore, Task> exercise)
    {
        await exercise(new InMemoryTriggerStore());
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, Clock()).EnsureCreatedAsync();
            await exercise(new SqliteTriggerStore(factory));
        }
        finally
        {
            Release(path);
        }
    }

    private static string TempDatabase() =>
        Path.Combine(Path.GetTempPath(), $"agent-core-trigger-{Guid.NewGuid():N}.db");

    private static TestFactory Factory(string path) =>
        new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);

    private static void Release(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
        File.Delete(path);
    }

    private static SessionSnapshot Snapshot(Guid sessionId) =>
        new(
            1,
            sessionId,
            1,
            new AgentDefinition(
                2,
                "examiner",
                1,
                new AgentIdentity("Alex", "role", "desc", "tone"),
                ["goal"],
                "instructions",
                new BehaviorPolicy("acknowledgeThenContinue", true, true),
                new ConversationPolicy("concise", true, "en", 256),
                new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
                new VoiceConfiguration(false, "default", 1.0),
                new ProviderPreferences(null, null),
                new Dictionary<string, string>()),
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            Now,
            Now, AgentInstanceId: Guid.NewGuid());

    private static async Task<List<string>> TableNamesAsync(AgentCoreDbContext db)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<int> ForeignKeyCountAsync(AgentCoreDbContext db, string table)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
        }

        return count;
    }

    private static async Task<string[]> ForeignKeyColumnsAsync(AgentCoreDbContext db, string table)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(3));
        return columns.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<System.Data.Common.DbConnection> OpenAsync(AgentCoreDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
