using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxStoreTests
{
    [Fact]
    public async Task Additive_migration_preserves_existing_session_bytes_and_drive_schema_without_reset()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-core-sandbox-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source="+path).Options);
        try
        {
            await using (var old = factory.CreateDbContext())
            {
                var previous = old.Database.GetMigrations().Last(m => m != "20261014003355_OpenSandboxOwnership");
                await old.GetService<IMigrator>().MigrateAsync(previous);
                old.Sessions.Add(new SessionRecord { SessionId=Guid.NewGuid().ToString(),AgentInstanceId=Guid.NewGuid().ToString(),
                    AgentId="preserved",AgentVersion=1,DefinitionJson="{\"exact\":\"before sandbox\"}",Status="Paused",Mode="Text",Revision=7,Title="Retained conversation" });
                await old.SaveChangesAsync();
            }
            await using (var upgraded = factory.CreateDbContext())
            {
                await upgraded.Database.MigrateAsync();
                var retained = await upgraded.Sessions.SingleAsync();
                Assert.Equal("{\"exact\":\"before sandbox\"}",retained.DefinitionJson); Assert.Equal(7,retained.Revision);Assert.Equal("Retained conversation",retained.Title);
                Assert.Empty(await upgraded.Set<SandboxLeaseRow>().ToListAsync());
                var connection = upgraded.Database.GetDbConnection(); await connection.OpenAsync();
                using var command = connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name IN ('DriveOperations','DriveAccountProfiles')";
                Assert.Equal(2L,await command.ExecuteScalarAsync());
            }
        }
        finally { SqliteClear(); if(File.Exists(path)) File.Delete(path); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Revisions_ownership_and_operation_uniqueness_survive_reopen(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-core-sandbox-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options);
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); }
            ISandboxStore store = sqlite ? new SqliteSandboxStore(factory) : new InMemorySandboxStore();
            var profile = new SandboxProfile("workbench", 1, "Workbench", "Local coding", true, "sha256:" + new string('a',64));
            await store.SaveProfileAsync(profile, 0);
            await store.SaveProfileAsync(profile with { Revision = 2, Name = "New" }, 1);
            Assert.Equal(profile, await store.GetProfileAsync(profile.Id, 1));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.SaveProfileAsync(profile with { Revision = 2 }, 1));
            var now = DateTimeOffset.UtcNow;
            var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, profile, SandboxLeaseState.Ready, "opaque", now, now.AddMinutes(30));
            await store.SaveLeaseAsync(lease, 0);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.SaveLeaseAsync(lease with { Revision = 2, AgentInstanceId = Guid.NewGuid() }, 1));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.SaveLeaseAsync(lease with { Revision = 2 }, 0));
            var execution = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation, Guid.NewGuid(), Guid.NewGuid(), "call-1", "hash", SandboxExecutionState.Dispatching, now);
            await store.SaveExecutionAsync(execution, true);
            Assert.Null(await store.GetExecutionAsync(Guid.NewGuid(), execution.Id));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.SaveExecutionAsync(execution with { Id = Guid.NewGuid() }, true));
            if (sqlite) store = new SqliteSandboxStore(factory);
            Assert.Equal(lease, await store.GetLeaseAsync(lease.SessionId));
            Assert.Equal(execution, await store.GetOperationAsync(lease.SessionId, execution.AgentRunId, "call-1"));
            Assert.Equal(2, (await store.ListProfilesAsync()).Single().Revision);
            await store.SaveExecutionAsync(execution with { State = SandboxExecutionState.Indeterminate }, false);
            Assert.Equal(SandboxExecutionState.Indeterminate, (await store.GetExecutionAsync(lease.SessionId, execution.Id))!.State);
        }
        finally { SqliteClear(); if (File.Exists(path)) File.Delete(path); }
    }
    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    private sealed class Factory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(options); }
}
