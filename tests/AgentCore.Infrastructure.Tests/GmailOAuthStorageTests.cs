using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed class GmailOAuthStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Secret_CAS_preserves_logical_revision_and_disconnect_fences_late_rotation(bool sqlite)
    {
        var path = Path.Combine(Path.GetTempPath(), "gmail-storage-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(path);
        try
        {
            ICredentialStore store;
            if (sqlite) { await using var db = factory.CreateDbContext(); await db.Database.MigrateAsync(); store = new SqliteCredentialStore(factory); }
            else store = new InMemoryCredentialStore(new());
            var now = DateTimeOffset.UtcNow;
            var profile = EmailAccountProfile.Gmail("verified@example.test") with { GoogleSubject = "subject", GrantedScopes = [EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope], ConnectionStatus = EmailConnectionStatus.Connected, AuthEpoch = 1 };
            var c = new Credential(Guid.NewGuid(), "Gmail", CredentialKind.Token, CredentialStatus.Active, CredentialRules.Metadata(null), [], "protected-refresh", 1, 1, now, now, CredentialPurpose.EmailAccount, profile, 1);
            await store.SaveAsync(c, 0);
            Assert.True(await store.TryRotateOAuthSecretAsync(c.CredentialId, 1, 1, "replacement"));
            Assert.False(await store.TryRotateOAuthSecretAsync(c.CredentialId, 1, 1, "stale"));
            await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => store.SaveAsync(c with { DisplayName = "stale metadata", Revision = 2 }, 1, expectedSecretRevision: 1).AsTask());
            c = (await store.GetAsync(c.CredentialId))!;
            Assert.Equal(1, c.Revision); Assert.Equal(2, c.SecretRevision);
            await store.SaveAsync(c with { ProtectedPayload = "", SecretRevision = 3, Revision = 2, EmailAccount = profile with { ConnectionStatus = EmailConnectionStatus.Disconnected, AuthEpoch = 2 } }, 1);
            Assert.False(await store.TryRotateOAuthSecretAsync(c.CredentialId, 1, 2, "resurrected"));
            if (sqlite) store = new SqliteCredentialStore(factory);
            Assert.Equal("", (await store.GetAsync(c.CredentialId))!.ProtectedPayload);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class Factory(string path) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options); }
}
