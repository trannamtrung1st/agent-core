using System.Net;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Testing;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Tests;

public sealed class EmailAccountIntegrationTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    public void Prohibited_DNS_answers_are_rejected_even_in_mixed_results(string address) =>
        Assert.Throws<AgentCoreException>(() => MailTransportPolicy.ValidateAddresses([IPAddress.Parse("8.8.8.8"), IPAddress.Parse(address)]));
    [Fact]
    public void Custom_endpoints_require_explicit_allowlist_and_TLS_enums_are_validated()
    {
        var profile = EmailAccountProfile.Custom("sender@example.test") with { Preset = EmailProviderPreset.Custom, ImapEndpoint = new("mail.example.test", 993, MailTlsMode.ImplicitTls) };
        Assert.Throws<AgentCoreException>(() => new MailTransportPolicy(new()).ValidateEndpoint(profile, false));
        new MailTransportPolicy(new() { AllowedCustomEndpoints = ["mail.example.test:993"] }).ValidateEndpoint(profile, false);
        Assert.Throws<ArgumentException>(() => (profile with { ImapEndpoint = new("mail.example.test", 993, (MailTlsMode)3) }).Validate());
        Assert.Throws<ArgumentException>(() => EmailAccountProfile.Custom("forged\r\nTo: someone@example.test").Validate());
    }
    [Fact]
    public async Task Current_SQLite_baseline_reopen_preserves_general_credentials_and_ciphertext()
    {
        var root = Path.Combine(Path.GetTempPath(), "email-reopen-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var factory = new DbFactory(root); var id = Guid.NewGuid().ToString("D");
            await new SqliteMemoryStore(factory, TimeProvider.System).EnsureCreatedAsync();
            await using (var db = factory.CreateDbContext())
            {
                // Omit Purpose to exercise the current baseline's General default.
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Credentials (CredentialId,DisplayName,Kind,Status,MetadataJson,AllowedOriginsJson,ProtectedPayload,ProtectionVersion,SecretRevision,Revision,CreatedAtUtc,UpdatedAtUtc) VALUES ({id},{"existing"},{"Password"},{"Active"},{"{}"},{"[]"},{"unchanged-ciphertext"},{1},{7},{7},{1L},{1L})");
            }
            var reopened = new DbFactory(root);
            await new SqliteMemoryStore(reopened, TimeProvider.System).EnsureCreatedAsync();
            var restored = await new SqliteCredentialStore(reopened).GetAsync(Guid.Parse(id));
            Assert.Equal(CredentialPurpose.General, restored!.Purpose); Assert.Equal(7, restored.Revision); Assert.Equal("unchanged-ciphertext", restored.ProtectedPayload);
            Assert.Equal(7, restored.SecretRevision); Assert.Equal(1, restored.ProtectionVersion); Assert.Null(restored.EmailAccount);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public void Legacy_Custom_approval_hash_is_preserved_and_OAuth_epoch_is_bound()
    {
        var draft = new EmailDraftSnapshot("legacy-custom-draft", ["to@example.test"], [], ["hidden@example.test"], "Subject", "Body",
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "mail", "sender@example.test", 1, 7,
            Guid.Parse("22222222-2222-2222-2222-222222222222"), 2);
        // Frozen canonical hash from the pre-OAuth format on 974159eb.
        Assert.Equal("c8ff714152994ae3a3069803b6a143003e2f9a24a855e8230802f70fe1f700be", EmailDraftNormalizer.ComputeSendActionHash(draft));
        Assert.NotEqual(EmailDraftNormalizer.ComputeSendActionHash(draft with { AuthEpoch = 1 }), EmailDraftNormalizer.ComputeSendActionHash(draft with { AuthEpoch = 2 }));
    }
    private sealed class DbFactory(string root) : IDbContextFactory<AgentCoreDbContext>
    {
        public DbContextOptions<AgentCoreDbContext> Options => new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={root}/email.db").Options;
        public AgentCoreDbContext CreateDbContext() => new(Options);
    }
}
