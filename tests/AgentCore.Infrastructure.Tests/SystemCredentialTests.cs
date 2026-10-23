using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class SystemCredentialTests
{

    [Fact]
    public void Credential_value_origin_and_metadata_rules_remain_bounded()
    {
        Assert.Throws<ArgumentException>(() => CredentialRules.Metadata(new Dictionary<string,string> { ["Key"] = "one", [" key "] = "two" }));
        Assert.Throws<ArgumentException>(() => CredentialRules.Metadata(new Dictionary<string,string> { ["value"] = new string('x', 2049) }));
        Assert.Throws<ArgumentException>(() => CredentialRules.Origins(["https://*.example.test"]));
        Assert.Throws<ArgumentException>(() => CredentialRules.Origins(["https://example.test/path"]));
        Assert.Throws<ArgumentException>(() => CredentialRules.ProtectedValue(""));
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

    private static SqliteFactory Factory(string root) => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={Path.Combine(root,"credentials.db")}").Options);
    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext> { public AgentCoreDbContext CreateDbContext() => new(options); }
}
