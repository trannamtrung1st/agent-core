using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class AutomationChildMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Populated_upgrade_preserves_children_frozen_receipts_buckets_history_and_reopens(bool mergedBranchHistory)
    {
        var path = Path.Combine(Path.GetTempPath(), $"child-upgrade-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var receipt = Guid.NewGuid(); var external = Guid.NewGuid(); var resource = Guid.NewGuid(); var bucketId = Guid.NewGuid();
        var boundedId = Guid.NewGuid();
        var emptyEvidence = JsonSerializer.Serialize(new { triggerSummary = "Core Event · run.failed", padding = "" });
        var boundedEvidence = JsonSerializer.Serialize(new { triggerSummary = "Core Event · run.failed", padding = new string('x', TriggerLimits.MaxEvidenceBytes - System.Text.Encoding.UTF8.GetByteCount(emptyEvidence)) });
        Assert.Equal(TriggerLimits.MaxEvidenceBytes, System.Text.Encoding.UTF8.GetByteCount(boundedEvidence));
        var subscription = new EventSubscriptionSnapshot(ids[2], owner, 7, "true", new(EventDispatchMode.CoalesceLatest, 60));
        string LegacyJson<T>(T value) => JsonSerializer.Serialize(value).Replace($",\"TriggerId\":\"{ids[2]}\"", "");
        try
        {
            await using (var db = new AgentCoreDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261009190059_EventDispatchDefault");
                await db.Database.OpenConnectionAsync(); var connection = (SqliteConnection)db.Database.GetDbConnection();
                await InsertAsync(connection, "WebhookEvents", new() { ["ResourceId"] = resource.ToString(), ["DisplayName"] = "Revoked historical source", ["EventKey"] = "historical.revoked", ["Status"] = (int)WebhookEventStatus.Revoked, ["Revision"] = 9 });
                for (var kind = 0; kind < 3; kind++)
                    await InsertAsync(connection, "Automations", new() { ["AutomationId"] = ids[kind].ToString(), ["AgentInstanceId"] = owner.AgentInstanceId.ToString(), ["ProfileId"] = owner.ProfileId.ToString(),
                        ["TriggerKind"] = kind, ["Status"] = kind == 0 ? (int)AutomationStatus.Active : kind == 1 ? (int)AutomationStatus.Disabled : (int)AutomationStatus.SuspendedPolicy, ["ScheduleJson"] = kind == 0 ? TriggerScheduleCodec.Serialize(new DailySchedule(1, new TimeOnly(9, 0), "UTC")) : null, ["EventId"] = kind == 1 ? resource.ToString() : null, ["CoreEventKey"] = kind == 2 ? "run.completed" : null,
                        ["TriggerRevision"] = 7, ["Revision"] = 11, ["Instructions"] = "Preserve instructions", ["Name"] = "Preserve name", ["PresetId"] = "review-recent-work", ["PresetVersion"] = 1 });
                await InsertAsync(connection, "CoreEventDeliveries", new() { ["EventId"] = receipt.ToString(), ["AutomationId"] = ids[2].ToString(), ["AgentInstanceId"] = owner.AgentInstanceId.ToString(), ["ProfileId"] = owner.ProfileId.ToString(),
                    ["SnapshotJson"] = LegacyJson(subscription), ["Status"] = (int)EventMatchStatus.FilterError, ["DecisionJson"] = JsonSerializer.Serialize(new EventFilterResult(null, "error", "filter-worker-budget", Attempt: 2, RetryAtUtc: DateTimeOffset.UtcNow.AddMinutes(1))) });
                var webhookSnapshot = subscription with { AutomationId = ids[1], TriggerId = ids[1], SourceKind = TriggerSourceKind.ApplicationEvent, ResourceId = resource };
                await InsertAsync(connection, "ExternalEventDeliveries", new() { ["EventId"] = external.ToString(), ["AutomationId"] = ids[1].ToString(), ["AgentInstanceId"] = owner.AgentInstanceId.ToString(), ["ProfileId"] = owner.ProfileId.ToString(), ["SnapshotJson"] = JsonSerializer.Serialize(webhookSnapshot), ["Status"] = 0 });
                var bucket = new CoreEventBucket(bucketId, subscription, DateTimeOffset.UtcNow.AddSeconds(60), [new(receipt, owner, "run.completed", DateTimeOffset.UtcNow, "{}")]);
                await InsertAsync(connection, "CoreEventBuckets", new() { ["BucketId"] = bucketId.ToString(), ["AutomationId"] = ids[2].ToString(), ["PayloadJson"] = LegacyJson(bucket) });
                await InsertAsync(connection, "TriggerOccurrences", new() { ["OccurrenceId"] = Guid.NewGuid().ToString(), ["DedupeKey"] = $"core:{receipt}:{ids[2]}", ["AutomationId"] = ids[2].ToString(), ["SourceKind"] = (int)TriggerSourceKind.CoreEvent, ["AgentInstanceId"] = owner.AgentInstanceId.ToString(), ["ProfileId"] = owner.ProfileId.ToString(), ["TriggerRevision"] = 7, ["EvidenceJson"] = JsonSerializer.Serialize(new { automationId = ids[2], instructions = "Historical instructions", triggerKind = "CoreEvent", triggerSummary = "Core Event · run.failed", triggerContext = new { eventId = receipt } }) });
                await InsertAsync(connection, "TriggerOccurrences", new() { ["OccurrenceId"] = boundedId.ToString(), ["DedupeKey"] = "historical-budget-limit", ["AutomationId"] = ids[2].ToString(), ["SourceKind"] = (int)TriggerSourceKind.CoreEvent, ["AgentInstanceId"] = owner.AgentInstanceId.ToString(), ["ProfileId"] = owner.ProfileId.ToString(), ["TriggerRevision"] = 7, ["EvidenceJson"] = boundedEvidence });
                if (mergedBranchHistory)
                {
                    await db.Database.MigrateAsync("20261010065035_ScopedInstanceConfiguration");
                    // This branch applied these before the BrowserPrivacy/ChildTriggers migrations arrived from main.
                    await ApplyBranchMigrationAsync(db, "20261010072141_DefinitionResourceIdentityScope");
                    await ApplyBranchMigrationAsync(db, "20261010082055_SessionReasoningPreference");
                }
                await new SqliteMemoryStore(new Factory(options), TimeProvider.System).EnsureCreatedAsync();
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Equal(3, await db.AutomationTriggers.CountAsync());
                Assert.All(await db.Automations.ToArrayAsync(), a => { Assert.Equal(11, a.Revision); Assert.Equal("review-recent-work", a.PresetId); Assert.Equal(a.AutomationId, Assert.Single(a.Triggers).TriggerId); Assert.Equal(7, a.Triggers.Single().Revision); });
            }
            await new SqliteMemoryStore(new Factory(options), TimeProvider.System).EnsureCreatedAsync();
            await using var reopened = new AgentCoreDbContext(options);
            Assert.Equal(new[] { 0, 4, 5 }, (await reopened.Automations.Select(a => a.Status).ToArrayAsync()).Order());
            var source = await reopened.WebhookEvents.SingleAsync(); Assert.Equal((int)WebhookEventStatus.Revoked, source.Status); Assert.Equal(9, source.Revision);
            Assert.IsType<DailySchedule>(TriggerScheduleCodec.Deserialize((await reopened.AutomationTriggers.SingleAsync(t => t.Kind == 0)).ScheduleJson!));
            var delivery = Assert.Single(await reopened.CoreEventDeliveries.ToArrayAsync());
            Assert.Equal(ids[2].ToString(), delivery.TriggerId);
            Assert.Equal((int)EventMatchStatus.FilterError, delivery.Status);
            var frozen = JsonSerializer.Deserialize<EventSubscriptionSnapshot>(delivery.SnapshotJson)!;
            Assert.Equal(ids[2], frozen.TriggerId); Assert.Equal(7, frozen.TriggerRevision); Assert.Equal("true", frozen.FilterExpression);
            Assert.Equal(2, JsonSerializer.Deserialize<EventFilterResult>(delivery.DecisionJson!)!.Attempt);
            Assert.Equal(ids[1].ToString(), (await reopened.ExternalEventDeliveries.SingleAsync()).TriggerId);
            var grouped = JsonSerializer.Deserialize<CoreEventBucket>((await reopened.CoreEventBuckets.SingleAsync()).PayloadJson)!;
            Assert.Equal(ids[2], grouped.Subscription.TriggerId); Assert.Equal(receipt, Assert.Single(grouped.Sources).EventId);
            var bounded = await reopened.TriggerOccurrences.SingleAsync(o => o.OccurrenceId == boundedId.ToString());
            Assert.Equal(boundedEvidence, TriggerText.RequireEvidence(bounded.EvidenceJson));
            Assert.Equal(ids[2].ToString(), bounded.TriggerId);
            var historical = await reopened.TriggerOccurrences.SingleAsync(o => o.OccurrenceId != boundedId.ToString());
            Assert.Equal(ids[2].ToString(), historical.TriggerId);
            using var evidence = JsonDocument.Parse(historical.EvidenceJson);
            Assert.Equal("run.failed", evidence.RootElement.GetProperty("source").GetProperty("key").GetString());
            Assert.Equal("Historical instructions", evidence.RootElement.GetProperty("instructions").GetString());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Incomplete_baseline_or_unknown_history_does_not_apply_merged_migrations(bool unknownHistory)
    {
        var path = Path.Combine(Path.GetTempPath(), $"child-untrusted-history-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        try
        {
            await using (var db = new AgentCoreDbContext(options))
            {
                await db.Database.MigrateAsync("20261010065035_ScopedInstanceConfiguration");
                await ApplyBranchMigrationAsync(db, "20261010082055_SessionReasoningPreference");
                if (unknownHistory)
                    await db.Database.ExecuteSqlRawAsync("INSERT INTO __EFMigrationsHistory VALUES ('20261010070000_UnknownBranchMigration', '10.0.12');");
                else
                    await db.Database.ExecuteSqlRawAsync("DELETE FROM __EFMigrationsHistory WHERE MigrationId = '20260925071140_P7DefinitionLifecycle';");
            }
            await Assert.ThrowsAsync<AgentCoreException>(() =>
                new SqliteMemoryStore(new Factory(options), TimeProvider.System).EnsureCreatedAsync().AsTask());
            await using var verify = new AgentCoreDbContext(options);
            Assert.DoesNotContain("20261010071652_AutomationChildTriggers", await verify.Database.GetAppliedMigrationsAsync());
            Assert.DoesNotContain("20261010071329_BrowserPrivacyAdministration", await verify.Database.GetAppliedMigrationsAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }

    private static async Task ApplyBranchMigrationAsync(AgentCoreDbContext db, string id)
    {
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations[id], db.Database.ProviderName!);
        var model = db.GetService<IModelRuntimeInitializer>().Initialize(migration.TargetModel, designTime: true);
        foreach (var command in db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, model))
            await db.Database.ExecuteSqlRawAsync(command.CommandText);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __EFMigrationsHistory VALUES ({id}, {"10.0.12"});");
    }

    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);
        public ValueTask<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(CreateDbContext());
    }

    private static async Task InsertAsync(SqliteConnection connection, string table, Dictionary<string, object?> values)
    {
        await using var schema = connection.CreateCommand(); schema.CommandText = $"PRAGMA table_info('{table}')";
        await using (var reader = await schema.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                if (!values.ContainsKey(reader.GetString(1)) && (reader.GetInt32(3) == 1 || reader.GetInt32(5) > 0) && reader.IsDBNull(4))
                    values[reader.GetString(1)] = reader.GetString(2) == "INTEGER" ? 0 : "{}";
        await using var insert = connection.CreateCommand();
        insert.CommandText = $"INSERT INTO {table} ({string.Join(',', values.Keys)}) VALUES ({string.Join(',', values.Keys.Select((_, i) => "$p" + i))})";
        var index = 0; foreach (var value in values.Values) insert.Parameters.AddWithValue("$p" + index++, value ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync();
    }
}
