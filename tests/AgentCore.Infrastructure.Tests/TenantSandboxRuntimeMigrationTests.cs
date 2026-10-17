using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class TenantSandboxRuntimeMigrationTests
{
    [Fact]
    public async Task Runtime_ownership_migration_retains_legacy_generation_receipt_and_JSON_bytes_without_adoption()
    {
        var path = Path.Combine(Path.GetTempPath(), "sandbox-runtime-upgrade-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path + ";Foreign Keys=True").Options);
        var now = DateTimeOffset.UtcNow; var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7,
            new SandboxProfile("legacy", 1, "Legacy", "", true, "sha256:" + new string('a', 64)), SandboxLeaseState.NeedsReconciliation, "private", now, now.AddMinutes(10));
        var receipt = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation, Guid.NewGuid(), Guid.NewGuid(), "uncertain", "legacy-hash", SandboxExecutionState.Indeterminate, now, Stdout: "retained bytes");
        var legacyLease = JsonNode.Parse(JsonSerializer.Serialize(lease, json))!.AsObject(); legacyLease.Remove("tenantId"); legacyLease.Remove("executorPrincipalId");
        var legacyExecution = JsonNode.Parse(JsonSerializer.Serialize(receipt, json))!.AsObject(); legacyExecution.Remove("tenantId"); legacyExecution.Remove("executorPrincipalId"); legacyExecution.Remove("revision");
        var leaseJson = legacyLease.ToJsonString(); var receiptJson = legacyExecution.ToJsonString();
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261017205857_TenantSandboxInstanceVerification");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SandboxSessionLeases (SessionId, AgentInstanceId, Revision, State, ExpiresAt, Json) VALUES ({lease.SessionId.ToString("D")}, {lease.AgentInstanceId.ToString("D")}, {lease.Revision}, {(int)lease.State}, {lease.ExpiresAt.ToUnixTimeMilliseconds()}, {leaseJson})");
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SandboxExecutions (Id, SessionId, AgentRunId, ToolCallId, Json) VALUES ({receipt.Id.ToString("D")}, {receipt.SessionId.ToString("D")}, {receipt.AgentRunId.ToString("D")}, {receipt.ToolCallId}, {receiptJson})");
                await db.Database.MigrateAsync();
            }
            var store = new SqliteSandboxStore(factory);
            Assert.Equal(lease, await store.GetLeaseAsync(lease.SessionId)); Assert.Equal(receipt, await store.GetExecutionAsync(lease.SessionId, receipt.Id));
            await using var reopened = factory.CreateDbContext();
            var l = await reopened.Set<SandboxLeaseRow>().SingleAsync(); var e = await reopened.Set<SandboxExecutionRow>().SingleAsync();
            Assert.Null(l.TenantId); Assert.Null(e.TenantId); Assert.Equal(0, e.Revision);
            Assert.Equal(leaseJson, l.Json); Assert.Equal(receiptJson, e.Json);
            Assert.Empty(await reopened.Set<TenantSessionRecord>().ToArrayAsync());
            await reopened.Database.OpenConnectionAsync();
            using var command = reopened.Database.GetDbConnection().CreateCommand(); command.CommandText = "PRAGMA foreign_key_check";
            using var result = await command.ExecuteReaderAsync(); Assert.False(await result.ReadAsync());
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
