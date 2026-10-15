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
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task State_observation_redacts_uncertain_lease_and_preserves_original_command_receipt(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "sandbox-state-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options);
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); }
            ISandboxStore store = sqlite ? new SqliteSandboxStore(factory) : new InMemorySandboxStore();
            var now = DateTimeOffset.UtcNow;
            var profile = new SandboxProfile("fixture", 1, "Fixture", "", true, "sha256:" + new string('a', 64));
            var lease = new SandboxLease(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, profile,
                SandboxLeaseState.NeedsReconciliation, "SECRET_PROVIDER_ID", now, now.AddMinutes(30));
            var execution = new SandboxExecution(Guid.NewGuid(), lease.SessionId, lease.AgentInstanceId, lease.Generation, Guid.NewGuid(), Guid.NewGuid(),
                "original", "hash", SandboxExecutionState.Indeterminate, now, Stdout: "SECRET_COMMAND_OUTPUT", CommandText: "SECRET_COMMAND");
            await store.SaveLeaseAsync(lease, 0); await store.SaveExecutionAsync(execution, true);
            var observed = await store.ReadStateAsync(new(lease.AgentInstanceId, Guid.NewGuid()), now);
            Assert.Equal("partial", observed.Availability); Assert.Null(observed.ActiveGenerations); Assert.Null(observed.ActiveCommands);
            Assert.Equal("reconciliation_required", observed.Code);
            Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(observed));
            Assert.Equal(lease, await store.GetLeaseAsync(lease.SessionId));
            Assert.Equal(execution, await store.GetOperationAsync(lease.SessionId, execution.AgentRunId, execution.ToolCallId));
            Assert.Equal(0, (await store.ReadStateAsync(new(Guid.NewGuid(), Guid.NewGuid()), now)).ActiveGenerations);
        }
        finally { SqliteClear(); if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Oci_upgrade_preserves_populated_profiles_policy_instance_pins_observations_and_live_receipts()
    {
        var path=Path.Combine(Path.GetTempPath(),"oci-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        var factory=new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source="+path).Options);
        var profile=new SandboxProfile("existing",3,"Existing","",true,"docker.io/agent-core/tool@sha256:"+new string('a',64),InstanceRevision:9);
        var now=DateTimeOffset.UtcNow;var lease=new SandboxLease(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),7,profile,SandboxLeaseState.Ready,"private-provider",now,now.AddMinutes(30));
        var receipt=new SandboxExecution(Guid.NewGuid(),lease.SessionId,lease.AgentInstanceId,lease.Generation,Guid.NewGuid(),Guid.NewGuid(),"uncertain","hash",SandboxExecutionState.Indeterminate,now,WorkspaceSyncStatus:"unknown");
        var observation=new SandboxProfileVerification(profile.Id,profile.Revision,now,true,"Historical verification");
        var json=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        try
        {
            await using(var old=factory.CreateDbContext())
            {
                var previous=old.Database.GetMigrations().Last(m=>string.CompareOrdinal(m,"20261014163203_OciSandboxRegistryAuthority")<0);
                await old.GetService<IMigrator>().MigrateAsync(previous);
                old.Add(new SandboxProfileRow {Id=profile.Id,Revision=3,Json=System.Text.Json.JsonSerializer.Serialize(profile,json)});
                old.Add(new SandboxVerificationRow {ProfileId=profile.Id,ProfileRevision=3,Json=System.Text.Json.JsonSerializer.Serialize(observation,json)});
                old.Add(new SandboxLeaseRow {SessionId=lease.SessionId.ToString("D"),AgentInstanceId=lease.AgentInstanceId.ToString("D"),Revision=7,State=(int)lease.State,ExpiresAt=lease.ExpiresAt.ToUnixTimeMilliseconds(),Json=System.Text.Json.JsonSerializer.Serialize(lease,json)});
                old.Add(new SandboxExecutionRow {Id=receipt.Id.ToString("D"),SessionId=lease.SessionId.ToString("D"),AgentRunId=receipt.AgentRunId.ToString("D"),ToolCallId=receipt.ToolCallId,Json=System.Text.Json.JsonSerializer.Serialize(receipt,json)});
                old.Sessions.Add(new SessionRecord {SessionId=lease.SessionId.ToString("D"),AgentInstanceId=lease.AgentInstanceId.ToString("D"),AgentId="historical",AgentVersion=4,DefinitionJson="{\"sandboxPolicy\":{\"defaultProfileId\":\"existing\",\"allowedProfileIds\":[\"existing\"]}}",Status="Paused",Mode="Text",Revision=11});
                await old.SaveChangesAsync();
            }
            var owners=new SqliteAgentInstanceStore(factory,new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System));
            await owners.InsertAsync(new(lease.AgentInstanceId,"historical",4,new("Owner","Test","","neutral"),AgentInstanceLifecycle.Active,now,now,SettingsOverrides:new() {SandboxProfileId=new("existing")}));
            await using(var upgraded=factory.CreateDbContext()) await upgraded.Database.MigrateAsync();
            var store=new SqliteSandboxStore(factory);
            Assert.Equal(profile,await store.GetProfileAsync(profile.Id,3));Assert.Equal(lease,await store.GetLeaseAsync(lease.SessionId));
            Assert.Equal(receipt,await store.GetExecutionAsync(lease.SessionId,receipt.Id));Assert.Equal(observation,await store.GetVerificationAsync(profile.Id,3));
            Assert.Equal("existing",(await owners.FindAsync(lease.AgentInstanceId))!.SettingsOverrides!.SandboxProfileId!.Value);
            var authority=new AgentCore.Infrastructure.Sandbox.SandboxImageAuthority(new(),store,TimeProvider.System);
            Assert.Equal("NeedsVerification",(await authority.InspectAsync(profile)).Status);
            await using var check=factory.CreateDbContext();Assert.Equal(11,(await check.Sessions.SingleAsync()).Revision);
        }
        finally { SqliteClear();if(File.Exists(path))File.Delete(path); }
    }
    [Fact]
    public async Task Additive_migration_preserves_existing_session_bytes_and_drive_schema_without_reset()
    {
        var path = Path.Combine(Path.GetTempPath(), "agent-core-sandbox-upgrade-"+Guid.NewGuid().ToString("N")+".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source="+path).Options);
        try
        {
            await using (var old = factory.CreateDbContext())
            {
                var previous = old.Database.GetMigrations().Last(m => string.CompareOrdinal(m, "20261014003355_OpenSandboxOwnership") < 0);
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
    public async Task Verification_observations_do_not_change_immutable_configuration_and_survive_reopen(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "sandbox-verification-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options);
        try
        {
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); }
            ISandboxStore store = sqlite ? new SqliteSandboxStore(factory) : new InMemorySandboxStore();
            var profile = new SandboxProfile("workbench", 1, "Workbench", "Coding", true, "sha256:" + new string('a',64));
            await store.SaveProfileAsync(profile, 0);
            var observation = new SandboxProfileVerification(profile.Id, profile.Revision, DateTimeOffset.UtcNow, false, "Initialization failed with exit code 7.", "initialization", 7);
            await store.SaveVerificationAsync(observation);
            var instanceObservation = new SandboxInstanceVerification(Guid.NewGuid(), 7, observation with { Stdout = "Hello, world!", Stderr = "bounded-error", DurationMs = 1234 });
            await store.SaveInstanceVerificationAsync(instanceObservation);
            await store.SaveProfileAsync(profile with { Revision = 2, InitializationScript = "true" }, 1);
            if (sqlite) store = new SqliteSandboxStore(factory);
            Assert.Equal(profile, await store.GetProfileAsync(profile.Id, 1));
            Assert.Equal(observation, await store.GetVerificationAsync(profile.Id, 1));
            Assert.Equal(instanceObservation, await store.GetInstanceVerificationAsync(instanceObservation.InstanceId, 7));
            Assert.Null(await store.GetInstanceVerificationAsync(instanceObservation.InstanceId, 8));
            var alternate = profile with { Id="alternate" };
            await store.SaveInstanceVerificationAsync(new(instanceObservation.InstanceId, 7, observation with { ProfileId=alternate.Id, RegistryCredentialRevision=2, RegistrySecretRevision=3 }));
            if (sqlite) store = new SqliteSandboxStore(factory);
            Assert.Equal(instanceObservation.Result, (await store.GetInstanceVerificationAsync(instanceObservation.InstanceId, 7, profile))!.Result);
            Assert.NotNull(await store.GetInstanceVerificationAsync(instanceObservation.InstanceId, 7, alternate with { RegistryCredentialRevision=2, RegistrySecretRevision=3 }));
            Assert.Null(await store.GetInstanceVerificationAsync(instanceObservation.InstanceId, 7, alternate with { RegistryCredentialRevision=3, RegistrySecretRevision=3 }));
            Assert.Null(await store.GetVerificationAsync(profile.Id, 2));
            await Assert.ThrowsAsync<AgentCoreException>(async () => await store.SaveVerificationAsync(observation with { ProfileRevision = 3 }));
        }
        finally { SqliteClear(); if (File.Exists(path)) File.Delete(path); }
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
