using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(CredentialPurpose.EmailAccount, false)]
    [InlineData(CredentialPurpose.GoogleDriveAccount, false)]
    [InlineData(CredentialPurpose.GoogleDriveAccount, true)]
    public async Task Cutover_audits_read_only_and_requires_reconnect_without_deleting_retained_grants(CredentialPurpose purpose, bool unstamped)
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("cutover@example.test", "Owner", "strong-owner-password", "Cutover", "cutover");
        var login = (await f.Identity.SignInAsync("cutover@example.test", "strong-owner-password"))!.Session;
        var initiator = new CredentialOAuthInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var id = await ConnectForCheck(f, owner.TenantId, initiator, purpose);
        var current = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        // Simulate an actual retained pre-cutover grant, never restamp it.
        var payload = f.Protector.Protect(id, 1, unstamped ? "private-unstamped" : GoogleOAuthRefreshGrant.Write("private-old-client", "retired-client"));
        await using (var db = f.Contexts!.CreateDbContext())
        {
            var row = await db.Credentials.SingleAsync(c => c.CredentialId == id.ToString("D")); row.ProtectedPayload = payload; await db.SaveChangesAsync();
        }
        var beforeKeys = Directory.GetFiles(Path.Combine(f.Root, "keys")).Order().ToArray();
        var report = await GoogleGrantAudit.ReadAsync(Path.Combine(f.Root, "store.db"), Path.Combine(f.Root, "keys"), "tenant-client");
        Assert.Equal(1, report[unstamped ? "unstamped" : "otherClient"]); Assert.Equal(1, report["affectedTenants"]);
        Assert.Equal(beforeKeys, Directory.GetFiles(Path.Combine(f.Root, "keys")).Order());
        Assert.Equal(payload, (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value.ProtectedPayload);
        using var http = new OAuthHttp(_ => throw new InvalidOperationException("An unproven grant cannot contact Google."));
        var provider = OAuthProvider(f, http, false, SharedOAuth());
        var service = CheckService(f, provider);
        var revision = (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision;
        var error = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => service.CheckAsync(owner.TenantId, id, initiator, revision, current.Revision).AsTask());
        Assert.Equal("oauth_reconnect_required", error.Code); Assert.Contains("saved grant was retained", error.Message);
        var reopened = (await new SqliteIamStore(f.Contexts!).FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(payload, reopened.ProtectedPayload); Assert.Equal(current.Revision, reopened.Revision);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        var runtimeError = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => provider.RuntimeTokenAsync(
            _ => ValueTask.FromResult(reopened), (_, _) => throw new InvalidOperationException("Unproven grant must not be changed by runtime refresh."), default).AsTask());
        Assert.Equal("oauth_reconnect_required", runtimeError.Code);
        // Removal is deliberate and goes through the existing original-human owner.
        var disconnected = await OAuthService(f, provider).DisconnectAsync(owner.TenantId, id, initiator, revision, current.Revision);
        Assert.False(disconnected.ProviderRevoked);
        reopened = (await new SqliteIamStore(f.Contexts!).FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Empty(reopened.ProtectedPayload); Assert.Equal(current.Revision + 1, reopened.Revision);
        AssertSafe(report, "private-unstamped", "private-old-client", payload);
        AssertSafe(await f.Iam.ReadAuditAsync(owner.TenantId, 100), "private-unstamped", "private-old-client", payload);
    }
}
