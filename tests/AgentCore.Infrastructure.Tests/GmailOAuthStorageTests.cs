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
    [Fact]
    public async Task Forward_migration_preserves_ids_bindings_custom_and_general_but_drops_Gmail_password()
    {
        var path = Path.Combine(Path.GetTempPath(), "gmail-upgrade-" + Guid.NewGuid().ToString("N") + ".db");
        var factory = new Factory(path); var gmail = Guid.NewGuid(); var custom = Guid.NewGuid(); var owner = Guid.NewGuid(); var binding = Guid.NewGuid();
        try
        {
            await using (var db = factory.CreateDbContext())
            {
                await db.GetService<IMigrator>().MigrateAsync("20261011182635_AccountScopedEmail");
                foreach (var id in new[] { gmail, custom })
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Credentials (CredentialId,DisplayName,Kind,Purpose,Status,MetadataJson,AllowedOriginsJson,ProtectedPayload,ProtectionVersion,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES ({id.ToString("D")},{"mail"},{"Password"},{"EmailAccount"},{"Active"},{"{}"},{"[]"},{"old-encrypted-password"},{1},{7},{1L},{1L})");
                    var legacy = new { Preset = id == gmail ? 0 : 1, EmailAddress = "sender@example.test", ImapUsername = "sender@example.test", SmtpUsername = "sender@example.test", ImapEndpoint = new { Host = "imap.gmail.com", Port = 993, TlsMode = 0 }, SmtpEndpoint = new { Host = "smtp.gmail.com", Port = 465, TlsMode = 0 } };
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO EmailAccountProfiles (CredentialId,ProfileJson) VALUES ({id.ToString("D")},{JsonSerializer.Serialize(legacy)})");
                }
                var instance = new AgentCore.Domain.Definitions.AgentInstance(owner, "general-assistant", 25, new("Test", "Assistant", "Test", "neutral"), AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                await new SqliteAgentInstanceStore(factory, new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System)).InsertAsync(instance);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AgentCredentialBindings (BindingId,AgentInstanceId,CredentialId,Reference,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES ({binding.ToString("D")},{owner.ToString("D")},{gmail.ToString("D")},{"mail"},{1},{1L},{1L})");
                await db.Database.MigrateAsync();
            }
            var preserved = Assert.Single(await new SqliteCredentialStore(factory).ListBindingsAsync(owner));
            Assert.Equal(binding, preserved.BindingId); Assert.Equal(gmail, preserved.CredentialId); Assert.Equal("mail", preserved.Reference);
            var store = new SqliteCredentialStore(factory);
            var upgraded = (await store.GetAsync(gmail))!;
            Assert.Equal(gmail, upgraded.CredentialId); Assert.Equal(CredentialKind.Token, upgraded.Kind);
            Assert.Equal("", upgraded.ProtectedPayload); Assert.Equal(8, upgraded.Revision);
            Assert.Equal(EmailAuthMethod.GoogleOAuth2, upgraded.EmailAccount!.AuthMethod);
            Assert.False(upgraded.EmailAccount.IsUsable); Assert.Null(upgraded.EmailAccount.GoogleSubject);
            Assert.Equal("sender@example.test", upgraded.EmailAccount.EmailAddress);
            var kept = (await store.GetAsync(custom))!;
            Assert.Equal(CredentialKind.Password, kept.Kind); Assert.Equal(7, kept.Revision); Assert.Equal("old-encrypted-password", kept.ProtectedPayload);
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(path)) File.Delete(path); }
    }
    private sealed class Factory(string path) : IDbContextFactory<AgentCoreDbContext>
    { public AgentCoreDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite("Data Source=" + path).Options); }
}
