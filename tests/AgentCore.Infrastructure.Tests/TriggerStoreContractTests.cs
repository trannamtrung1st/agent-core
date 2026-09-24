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
    public async Task Both_stores_create_list_update_cancel_and_isolate_owners()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var other = new TriggerOwner(InstanceB, ProfileB);
            var created = await service.CreateAsync(Draft(owner, "Call John", OneShot()));
            Assert.Equal(1, created.Revision);
            Assert.Equal(1, created.ScheduleRevision);
            Assert.Equal(TriggerRegistrationStatus.Active, created.Status);
            Assert.Equal(TriggerAuthorizationOrigin.CurrentUserTurn, created.Provenance.AuthorizationOrigin);
            Assert.Equal(SourceSessionId, created.Provenance.SourceSessionId);
            Assert.Equal(Now, created.Provenance.CreatedAt);
            Assert.Equal(owner, created.Owner);
            Assert.Equal(1, await service.CountActiveAsync(owner));
            Assert.Equal(0, await service.CountActiveAsync(other));

            time.Advance(TimeSpan.FromSeconds(1));
            var listed = await service.ListAsync(owner, TriggerRegistrationStatus.Active);
            var visible = Assert.Single(listed);
            Assert.Equal(created.RegistrationId, visible.RegistrationId);
            Assert.Empty(await service.ListAsync(other, null));
            Assert.Null(await service.GetAsync(other, created.RegistrationId));

            var same = await service.UpdateAsync(
                owner,
                created.RegistrationId,
                1,
                TriggerRegistrationChange.IntentOnly("  Call John  "));
            Assert.Equal(1, same.Revision);
            Assert.Equal("Call John", same.Intent);

            time.Advance(TimeSpan.FromSeconds(1));
            var renamed = await service.UpdateAsync(
                owner,
                created.RegistrationId,
                1,
                TriggerRegistrationChange.IntentOnly("Call later"));
            Assert.Equal(2, renamed.Revision);
            Assert.Equal(1, renamed.ScheduleRevision);
            Assert.Equal("Call later", renamed.Intent);

            var stale = await Assert.ThrowsAsync<AgentCoreException>(() => service.UpdateAsync(
                owner,
                created.RegistrationId,
                1,
                TriggerRegistrationChange.IntentOnly("nope")).AsTask());
            Assert.Equal("Conflict", stale.Code);
            Assert.Equal("Call later", (await service.GetAsync(owner, created.RegistrationId))!.Intent);

            time.Advance(TimeSpan.FromSeconds(1));
            var weekly = Weekly();
            var rescheduled = await service.UpdateAsync(
                owner,
                created.RegistrationId,
                2,
                TriggerRegistrationChange.ScheduleOnly(weekly, Now.AddDays(5), null));
            Assert.Equal(3, rescheduled.Revision);
            Assert.Equal(2, rescheduled.ScheduleRevision);
            Assert.True(weekly.SemanticEquals(rescheduled.Schedule));
            Assert.Equal(Now.AddDays(5), rescheduled.NextOccurrenceAtUtc);

            var denied = await Assert.ThrowsAsync<AgentCoreException>(() => service.CancelAsync(
                other,
                created.RegistrationId,
                3).AsTask());
            Assert.Equal("NotFound", denied.Code);
            Assert.DoesNotContain("Call later", denied.Message, StringComparison.Ordinal);

            var staleCancel = await Assert.ThrowsAsync<AgentCoreException>(() => service.CancelAsync(
                owner,
                created.RegistrationId,
                2).AsTask());
            Assert.Equal("Conflict", staleCancel.Code);
            Assert.Equal(TriggerRegistrationStatus.Active, (await service.GetAsync(owner, created.RegistrationId))!.Status);

            time.Advance(TimeSpan.FromSeconds(1));
            var cancelled = await service.CancelAsync(owner, created.RegistrationId, 3);
            Assert.Equal(TriggerRegistrationStatus.Cancelled, cancelled.Status);
            Assert.Equal(4, cancelled.Revision);
            Assert.Equal(2, cancelled.ScheduleRevision);
            var repeated = await service.CancelAsync(owner, created.RegistrationId, 3);
            Assert.Equal(cancelled.Revision, repeated.Revision);
            Assert.Equal(0, await service.CountActiveAsync(owner));
            Assert.Empty(await service.ListAsync(owner, TriggerRegistrationStatus.Active));
            Assert.Equal(created.RegistrationId, Assert.Single(await service.ListAsync(owner, TriggerRegistrationStatus.Cancelled)).RegistrationId);

            var updateCancelled = await Assert.ThrowsAsync<AgentCoreException>(() => service.UpdateAsync(
                owner,
                created.RegistrationId,
                4,
                TriggerRegistrationChange.IntentOnly("again")).AsTask());
            Assert.Equal("ValidationError", updateCancelled.Code);
        });
    }

    [Fact]
    public async Task ListAsync_returns_newest_created_first()
    {
        await ForEachStore(async store =>
        {
            var time = Clock();
            var service = Service(store, time);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var a = await service.CreateAsync(Draft(owner, "A", OneShot()));
            time.Advance(TimeSpan.FromSeconds(1));
            var b = await service.CreateAsync(Draft(owner, "B", OneShot()));
            time.Advance(TimeSpan.FromSeconds(1));
            var c = await service.CreateAsync(Draft(owner, "C", OneShot()));

            var listed = await service.ListAsync(owner, null);
            Assert.Equal([c.RegistrationId, b.RegistrationId, a.RegistrationId], listed.Select(row => row.RegistrationId).ToArray());
        });
    }

    [Fact]
    public async Task Occurrence_admission_dedupes_and_hides_other_owners()
    {
        await ForEachStore(async store =>
        {
            var service = Service(store, Clock());
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var other = new TriggerOwner(InstanceB, ProfileB);
            var draft = new TriggerOccurrenceDraft(
                owner,
                "order-status|event-1",
                null,
                TriggerSourceKind.ApplicationEvent,
                null,
                Now,
                """{"orderReference":"A1","status":"shipped"}""",
                Guid.Parse("019944af-0006-7000-8000-0000000000e1"),
                null);
            var admitted = await service.AdmitOccurrenceAsync(draft);
            Assert.Equal(TriggerOccurrenceAdmitKind.Admitted, admitted.Kind);
            Assert.Equal(OccurrenceRoutingDisposition.Pending, admitted.Occurrence.Disposition);
            Assert.Equal(0, admitted.Occurrence.RoutingRevision);
            Assert.Null(admitted.Occurrence.ClaimId);
            var duplicate = await service.AdmitOccurrenceAsync(draft);
            Assert.Equal(TriggerOccurrenceAdmitKind.Duplicate, duplicate.Kind);
            Assert.Equal(admitted.Occurrence.OccurrenceId, duplicate.Occurrence.OccurrenceId);
            Assert.Null(await service.GetOccurrenceAsync(other, admitted.Occurrence.OccurrenceId));
            Assert.Equal(
                admitted.Occurrence.OccurrenceId,
                (await service.GetOccurrenceAsync(owner, admitted.Occurrence.OccurrenceId))!.OccurrenceId);

            var otherAdmitted = await service.AdmitOccurrenceAsync(draft with { Owner = other });
            Assert.Equal(TriggerOccurrenceAdmitKind.Admitted, otherAdmitted.Kind);
            Assert.NotEqual(admitted.Occurrence.OccurrenceId, otherAdmitted.Occurrence.OccurrenceId);
            var duplicateOwner = await service.AdmitOccurrenceAsync(draft);
            Assert.Equal(TriggerOccurrenceAdmitKind.Duplicate, duplicateOwner.Kind);
            Assert.Equal(admitted.Occurrence.OccurrenceId, duplicateOwner.Occurrence.OccurrenceId);
            var duplicateOther = await service.AdmitOccurrenceAsync(draft with { Owner = other });
            Assert.Equal(TriggerOccurrenceAdmitKind.Duplicate, duplicateOther.Kind);
            Assert.Equal(otherAdmitted.Occurrence.OccurrenceId, duplicateOther.Occurrence.OccurrenceId);

            var invalid = await Assert.ThrowsAsync<AgentCoreException>(() => service.AdmitOccurrenceAsync(
                draft with { DedupeKey = "order-status|event-2", EvidenceJson = "not-json" }).AsTask());
            Assert.Equal("ValidationError", invalid.Code);
            Assert.Null(await service.GetOccurrenceAsync(owner, Guid.Parse("019944af-0006-7000-8000-000000000099")));
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
            var service = Service(new SqliteTriggerStore(factory), time);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await service.CreateAsync(Draft(owner, "Call John", OneShot()));
            var admitted = await service.AdmitOccurrenceAsync(new TriggerOccurrenceDraft(
                owner,
                "registration|1|1758607200000",
                created.RegistrationId,
                TriggerSourceKind.Schedule,
                created.NextOccurrenceAtUtc,
                Now,
                "{}",
                null,
                created.ScheduleRevision));

            var reopened = Service(new SqliteTriggerStore(factory), time);
            var loaded = await reopened.GetAsync(owner, created.RegistrationId);
            Assert.NotNull(loaded);
            Assert.Equal("Call John", loaded!.Intent);
            Assert.True(OneShot().SemanticEquals(loaded.Schedule));
            Assert.Equal("Asia/Ho_Chi_Minh", ((OneShotSchedule)loaded.Schedule).TimeZoneId);
            Assert.Equal(new DateOnly(2026, 9, 24), ((OneShotSchedule)loaded.Schedule).LocalDate);
            Assert.Equal(new TimeOnly(9, 0), ((OneShotSchedule)loaded.Schedule).LocalTime);
            var occurrence = await reopened.GetOccurrenceAsync(owner, admitted.Occurrence.OccurrenceId);
            Assert.Equal(admitted.Occurrence.DedupeKey, occurrence!.DedupeKey);
            Assert.Equal(created.RegistrationId, occurrence.RegistrationId);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Previous_schema_upgrade_keeps_triggers_after_session_and_memory_deletion()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        var time = Clock();
        try
        {
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync("20260923110000_UserMemory");
            }

            await new SqliteMemoryStore(factory, time).SaveAsync(Snapshot(SourceSessionId), 0);
            await using (var db = await factory.CreateDbContextAsync())
            {
                await db.Database.MigrateAsync();
                var tables = await TableNamesAsync(db);
                Assert.Contains("TriggerRegistrations", tables);
                Assert.Contains("TriggerOccurrences", tables);
                Assert.DoesNotContain(tables, name => name.Contains("Timer", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(0, await ForeignKeyCountAsync(db, "TriggerRegistrations"));
                Assert.Equal(0, await ForeignKeyCountAsync(db, "TriggerOccurrences"));
            }

            var owner = new TriggerOwner(InstanceA, ProfileA);
            var service = Service(new SqliteTriggerStore(factory), time);
            var created = await service.CreateAsync(Draft(owner, "Call John", OneShot()));
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

            var loaded = await Service(new SqliteTriggerStore(factory), time).GetAsync(owner, created.RegistrationId);
            Assert.Equal("Call John", loaded!.Intent);
            Assert.Equal(SourceSessionId, loaded.Provenance.SourceSessionId);
            Assert.Equal(owner, loaded.Owner);
            Assert.Equal(TriggerRegistrationStatus.Active, loaded.Status);
        }
        finally
        {
            Release(path);
        }
    }

    private static TriggerRegistrationDraft Draft(TriggerOwner owner, string intent, TriggerSchedule schedule) =>
        new(
            owner,
            intent,
            schedule,
            schedule is OneShotSchedule oneShot ? oneShot.AtUtc : null,
            null,
            TriggerAuthorizationOrigin.CurrentUserTurn,
            SourceSessionId,
            Guid.Parse("019944af-0006-7000-8000-0000000000e9"));

    private static OneShotSchedule OneShot() =>
        new(Now.AddDays(1), "Asia/Ho_Chi_Minh", new DateOnly(2026, 9, 24), new TimeOnly(9, 0));

    private static WeeklySchedule Weekly() =>
        new(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London");

    private static TriggerRegistrationService Service(ITriggerStore store, TimeProvider time) =>
        new(
            store,
            new DeterministicIdGenerator(
                Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-0007-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-0007-7000-8000-0000000000ff")]),
            time);

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
                1,
                "examiner",
                1,
                new AgentIdentity("Alex", "role", "desc", "tone"),
                ["goal"],
                "instructions",
                new BehaviorPolicy("acknowledgeThenContinue", true, true),
                new ConversationPolicy("concise", true, "en", 256),
                new InitiativePolicy(true, 8000, 30000, 1, ["longSilence"]),
                new VoiceConfiguration(false, "default", 1.0),
                new ProviderPreferences("primary-llm", null, null),
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
            Now);

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
