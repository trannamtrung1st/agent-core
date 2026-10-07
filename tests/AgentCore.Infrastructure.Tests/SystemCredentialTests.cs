using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SystemCredentialTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_credential_rotation_isolation_disable_unbind_and_bound_delete(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-journey-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var instances = new InMemoryAgentInstanceStore(); var a = NewInstance(); var b = NewInstance();
            IAgentInstanceStore owners = instances; ICredentialStore store; IAgentCredentialBindingStore bindings;
            var factory = Factory(root);
            if (sqlite)
            {
                await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync();
                owners = new SqliteAgentInstanceStore(factory, new SystemIdGenerator(TimeProvider.System)); var adapter = new SqliteCredentialStore(factory); store = adapter; bindings = adapter;
            }
            else { var adapter = new InMemoryCredentialStore(instances); store = adapter; bindings = adapter; }
            await owners.InsertAsync(a); await owners.InsertAsync(b);
            var protector = new LocalCredentialProtector(Path.Combine(root, "keys"));
            var service = new CredentialService(store, bindings, protector, owners, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var c = await service.CreateAsync("Shared login", "Password", new Dictionary<string,string> { ["username"] = "operator@example.test", ["canUseBrowser"] = "true" }, ["http://localhost:5088"], "first-secret");
            var ga = await service.BindAsync(a.InstanceId, c.CredentialId, "PRIMARY", a.Revision);
            var gb = await service.BindAsync(b.InstanceId, c.CredentialId, "shared", b.Revision);
            Assert.Equal("primary", Assert.Single(await service.SafeMetadataAsync(a.InstanceId)).Reference);
            Assert.Equal("shared", Assert.Single(await service.SafeMetadataAsync(b.InstanceId)).Reference);
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ResolvePasswordAsync(b.InstanceId, "primary", "http://localhost:5088").AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => service.BindAsync(a.InstanceId, c.CredentialId, "duplicate", a.Revision).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ResolvePasswordAsync(a.InstanceId, "primary", "https://other.test").AsTask());
            c = await service.ReplaceAsync(c.CredentialId, c.Revision, "replacement-secret");
            Assert.Equal(2, c.BindingCount);
            Assert.Equal("replacement-secret", await service.ResolvePasswordAsync(a.InstanceId, "primary", "http://localhost:5088"));
            Assert.Equal("replacement-secret", await service.ResolvePasswordAsync(b.InstanceId, "shared", "http://localhost:5088"));
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ReplaceAsync(c.CredentialId, 1, "stale").AsTask());
            var safe = JsonSerializer.Serialize(await service.ListAsync());
            Assert.DoesNotContain("replacement-secret", safe); Assert.DoesNotContain("ProtectedPayload", safe); Assert.DoesNotContain("ProtectionVersion", safe);
            c = await service.UpdateAsync(c.CredentialId, c.Revision, c.DisplayName, "Disabled", c.Metadata, c.AllowedOrigins);
            Assert.Empty(await service.SafeMetadataAsync(a.InstanceId));
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ResolvePasswordAsync(a.InstanceId, "primary", "http://localhost:5088").AsTask());
            c = await service.UpdateAsync(c.CredentialId, c.Revision, c.DisplayName, "Active", c.Metadata, c.AllowedOrigins);
            await service.UnbindAsync(a.InstanceId, ga.BindingId, ga.Revision, a.Revision);
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ResolvePasswordAsync(a.InstanceId, "primary", "http://localhost:5088").AsTask());
            Assert.Equal("replacement-secret", await service.ResolvePasswordAsync(b.InstanceId, "shared", "http://localhost:5088"));
            await Assert.ThrowsAsync<AgentCoreException>(() => service.DeleteAsync(c.CredentialId, c.Revision).AsTask());
            await service.UnbindAsync(b.InstanceId, gb.BindingId, gb.Revision, b.Revision);
            await service.DeleteAsync(c.CredentialId, c.Revision); Assert.Empty(await service.ListAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Protection_reopens_rejects_copied_and_corrupt_payload_and_sqlite_contains_no_plaintext()
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-protection-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var id = Guid.NewGuid(); const string value = "known-private-value-🗝-case-sensitive";
            var protector = new LocalCredentialProtector(Path.Combine(root, "keys")); var payload = protector.Protect(id, 1, value);
            var reopened = new LocalCredentialProtector(Path.Combine(root, "keys")); Assert.Equal(value, reopened.Unprotect(id, 1, payload));
            Assert.Equal("credential_unavailable", Assert.Throws<AgentCoreException>(() => reopened.Unprotect(Guid.NewGuid(), 1, payload)).Code);
            Assert.Throws<AgentCoreException>(() => reopened.Unprotect(id, 2, payload)); Assert.Throws<AgentCoreException>(() => reopened.Unprotect(id, 1, "corrupt"));
            Assert.Throws<AgentCoreException>(() => new LocalCredentialProtector(Path.Combine(root, "missing-key-ring")).Unprotect(id, 1, payload));
            var factory = Factory(root); await using (var db = factory.CreateDbContext()) await db.Database.MigrateAsync();
            var store = new SqliteCredentialStore(factory); var now = DateTimeOffset.UtcNow;
            await store.SaveAsync(new(id, "Protected", CredentialKind.Password, CredentialStatus.Active, CredentialRules.Metadata(null), ["https://example.test"], payload, 1, 1, now, now), 0);
            var loaded = await new SqliteCredentialStore(factory).GetAsync(id); Assert.Equal(value, reopened.Unprotect(id, 1, loaded!.ProtectedPayload));
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(root, "*.db*")) Assert.DoesNotContain(value, System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Unknown_kind_wrong_sink_alias_uniqueness_metadata_bounds_and_archived_agent_are_denied()
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-policy-" + Guid.NewGuid().ToString("N"));
        try
        {
            var instances = new InMemoryAgentInstanceStore(); var a = NewInstance(); var archived = NewInstance() with { Lifecycle = AgentInstanceLifecycle.Archived };
            await instances.InsertAsync(a); await instances.InsertAsync(archived);
            var store = new InMemoryCredentialStore(instances);
            var service = new CredentialService(store, store, new LocalCredentialProtector(root), instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            await Assert.ThrowsAsync<AgentCoreException>(() => service.CreateAsync("Invalid", "Custom", null, [], "value").AsTask());
            var key = await service.CreateAsync("API", "ApiKey", null, ["https://example.test"], "key-value");
            await service.BindAsync(a.InstanceId, key.CredentialId, "primary", a.Revision);
            await Assert.ThrowsAsync<AgentCoreException>(() => service.ResolvePasswordAsync(a.InstanceId, "primary", "https://example.test").AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => service.BindAsync(archived.InstanceId, key.CredentialId, "archived", archived.Revision).AsTask());
            var second = await service.CreateAsync("Other", "Password", null, [], "password");
            await Assert.ThrowsAsync<AgentCoreException>(() => service.BindAsync(a.InstanceId, second.CredentialId, "primary", a.Revision).AsTask());
            await Assert.ThrowsAsync<AgentCoreException>(() => service.BindAsync(a.InstanceId, second.CredentialId, "other", a.Revision + 1).AsTask());
            Assert.Throws<ArgumentException>(() => CredentialRules.Metadata(new Dictionary<string,string> { ["Key"] = "one", [" key "] = "two" }));
            Assert.Throws<ArgumentException>(() => CredentialRules.Metadata(new Dictionary<string,string> { ["value"] = new string('x',2049) }));
            Assert.Throws<ArgumentException>(() => CredentialRules.Origins(["https://*.example.test"]));
            Assert.Throws<ArgumentException>(() => CredentialRules.Origins(["https://example.test/path"]));
            Assert.Throws<ArgumentException>(() => CredentialRules.ProtectedValue(""));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Unavailable_key_directory_returns_bounded_error()
    {
        var file = Path.GetTempFileName();
        try
        {
            var protector = new LocalCredentialProtector(file);
            var error = Assert.Throws<AgentCoreException>(() => protector.Protect(Guid.NewGuid(), 1, "private"));
            Assert.Equal("credential_unavailable", error.Code);
            Assert.DoesNotContain(file, error.Message);
            Assert.DoesNotContain("private", error.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Upgrade_drops_legacy_rows_without_importing_or_touching_profiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-upgrade-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = Factory(root); var owner = Guid.NewGuid();
            var profile = Path.Combine(root, "profiles", owner.ToString("D")); Directory.CreateDirectory(profile);
            await File.WriteAllTextAsync(Path.Combine(profile, "Cookies"), "legacy-profile-marker");
            await using (var before = factory.CreateDbContext())
            {
                await before.GetService<IMigrator>().MigrateAsync("20261007014134_UnifiedAgentWorkspace");
                await before.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ApplicationConnections (ConnectionId, AgentInstanceId, Kind, DisplayName, BaseUrl, TrustedOriginsJson, Status, ProfileKey, Revision, CreatedAtUtc, UpdatedAtUtc) VALUES ({Guid.NewGuid().ToString("D")}, {owner.ToString("D")}, {"nopCommerce"}, {"Legacy"}, {"https://store.test"}, {"[]"}, {"Connected"}, {owner.ToString("D")}, {1L}, {1L}, {1L})");
            }
            await using (var after = factory.CreateDbContext())
            {
                await after.Database.MigrateAsync();
                Assert.Empty(await after.Credentials.ToArrayAsync()); Assert.Empty(await after.AgentCredentialBindings.ToArrayAsync());
                var connection = after.Database.GetDbConnection(); await connection.OpenAsync();
                await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ApplicationConnections'";
                Assert.Equal(0L, await command.ExecuteScalarAsync());
            }
            Assert.Equal("legacy-profile-marker", await File.ReadAllTextAsync(Path.Combine(profile, "Cookies")));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    private static AgentInstance NewInstance() => new(Guid.NewGuid(), "secretary", 3, new("Test", "Secretary", "Testing", "neutral"), AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static SqliteFactory Factory(string root) => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root,"credentials.db")}").Options);
    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
}
