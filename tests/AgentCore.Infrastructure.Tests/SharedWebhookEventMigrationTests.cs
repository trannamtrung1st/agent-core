using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SharedWebhookEventMigrationTests
{
    [Fact]
    public async Task Migration_preserves_event_identity_authentication_subscriptions_and_received_history()
    {
        var path = Path.Combine(Path.GetTempPath(), $"shared-events-{Guid.NewGuid():N}.sqlite");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var id = Guid.NewGuid().ToString("D"); var key = Guid.NewGuid().ToString("D");
        try
        {
            await using (var db = new AgentCoreDbContext(options))
            {
                await db.GetService<IMigrator>().MigrateAsync("20261008134257_DurableCompletionInbox");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExternalEventSources (SourceId, DisplayName, Kind, SourceKey, CredentialHash, Status, Revision, CreatedAtUtc, UpdatedAtUtc) VALUES ({id}, 'Invoice signal', 0, {key}, 'protected-hash', 0, 3, 1000, 2000)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExternalEvents (EventId, SourceId, SourceEventId, EventType, OccurredAtUtc, AdmittedAtUtc, EvidenceJson) VALUES ('receipt-1', {id}, 'invoice-1', 'order.placed', 1000, 2000, '{{}}')");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ExternalEventDeliveries (EventId, AutomationId, AgentInstanceId, ProfileId, Status) VALUES ('receipt-1', 'automation-1', 'instance-1', 'profile-1', 0)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Automations (AutomationId, AgentInstanceId, ProfileId, Status, Name, Instructions, TriggerKind, TriggerRevision, OccurrenceCount, Revision, AuthorizationOrigin, CreatedAtUtc, UpdatedAtUtc, RequiresVision, RequiresTools, EventSourceId, EventType, ExecutionTargetKind) VALUES ('automation-1', 'instance-1', 'profile-1', 0, 'Review invoice', 'Untrusted invoice review', 1, 1, 0, 2, 1, 1000, 2000, 0, 0, {id}, 'order.placed', 0)");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO TriggerOccurrences (OccurrenceId, DedupeKey, AutomationId, AgentInstanceId, ProfileId, SourceKind, ObservedAtUtc, AdmittedAtUtc, EvidenceJson, SourceEventId, TriggerRevision, Disposition, RoutingRevision, ExecutionTargetKind) VALUES ('occurrence-1', {"order.placed:" + id + ":invoice-1:automation-1"}, 'automation-1', 'instance-1', 'profile-1', 2, 1000, 2000, '{{}}', 'receipt-1', 1, 0, 0, 0)");
                await db.Database.MigrateAsync();
                Assert.False(db.Database.HasPendingModelChanges());
            }
            await using var reopened = new AgentCoreDbContext(options);
            await reopened.Database.MigrateAsync();
            var resource = await reopened.WebhookEvents.SingleAsync();
            Assert.Equal(id, resource.ResourceId); Assert.Equal("event." + key.Replace("-", ""), resource.EventKey);
            Assert.Equal("protected-hash", resource.CredentialHash); Assert.Equal(3, resource.Revision);
            Assert.Equal(1000, resource.CreatedAtUtc); Assert.Equal(2000, resource.UpdatedAtUtc);
            Assert.Equal(id, (await reopened.Automations.SingleAsync()).EventId);
            Assert.Equal(id, (await reopened.ExternalEvents.SingleAsync()).ResourceId);
            Assert.Equal(0, (await reopened.ExternalEventDeliveries.SingleAsync()).Status);
            Assert.Equal("event:" + id + ":invoice-1:automation-1", (await reopened.TriggerOccurrences.SingleAsync()).DedupeKey);
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); }
    }
}
