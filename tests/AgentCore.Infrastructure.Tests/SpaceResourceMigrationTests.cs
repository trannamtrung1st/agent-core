using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;
public sealed class SpaceResourceMigrationTests
{
    [Fact]
    public async Task Space_source_migrations_preserve_every_existing_guard_and_exact_approval_table_on_empty_roundtrip()
    {
        var path=Path.Combine(Path.GetTempPath(),"space-resource-migration-"+Guid.NewGuid().ToString("N")+".db");
        try
        {
            await using var db=new AgentCoreDbContext(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source="+path+";Foreign Keys=True").Options);
            var migrator=db.GetService<IMigrator>();
            async Task<Dictionary<string,string>> Schema()
            {
                await db.Database.OpenConnectionAsync(); await using var command=db.Database.GetDbConnection().CreateCommand();
                command.CommandText="SELECT name,sql FROM sqlite_master WHERE type='trigger' OR name='TenantApprovalRequests' ORDER BY name";
                var rows=new Dictionary<string,string>(); await using var reader=await command.ExecuteReaderAsync();
                while(await reader.ReadAsync())rows.Add(reader.GetString(0),reader.GetString(1)); return rows;
            }
            await migrator.MigrateAsync("20261025060000_ContextSuccessor"); var baseline=await Schema();
            await migrator.MigrateAsync("20261025073900_SpaceFileResources"); var sources=await Schema();
            Assert.Equal(baseline.Count+5,sources.Count); foreach(var row in baseline)Assert.Equal(row.Value,sources[row.Key]);
            await migrator.MigrateAsync("20261025080000_SpaceResourceApprovals"); var current=await Schema();
            Assert.Equal(sources.Count+2,current.Count);
            foreach(var row in sources.Where(r=>r.Key!="TenantApprovalRequests"))Assert.Equal(row.Value,current[row.Key]);
            Assert.Contains("/space/resources/",current["TenantApprovalRequests"]);
            Assert.Contains("Space source requires its exact current",current["TR_SpaceSources_Run_Insert"]);
            Assert.False(db.Database.HasPendingModelChanges());
            await migrator.MigrateAsync("20261025073900_SpaceFileResources"); Assert.Equal(sources,await Schema());
            await migrator.MigrateAsync("20261025060000_ContextSuccessor"); Assert.Equal(baseline,await Schema());
            await migrator.MigrateAsync("20261025080000_SpaceResourceApprovals"); Assert.Equal(current,await Schema()); Assert.False(db.Database.HasPendingModelChanges());
            await using var check=db.Database.GetDbConnection().CreateCommand();check.CommandText="PRAGMA foreign_key_check";
            await using var violations=await check.ExecuteReaderAsync();Assert.False(await violations.ReadAsync());
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();File.Delete(path);File.Delete(path+"-wal");File.Delete(path+"-shm");}
    }
}
