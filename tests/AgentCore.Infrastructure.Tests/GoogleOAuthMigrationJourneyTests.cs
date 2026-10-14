using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class GoogleEmailOAuthTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Legacy_grant_refresh_uses_its_original_client_or_blocks_without_HTTP_or_secret_loss(bool retainLegacy)
    {
        using var f = new Fixture(); var account = await f.Create(); var start = await f.Service.StartAsync(account.CredentialId, 1, f.Owner, default); await f.Complete(start);
        var saved = (await f.Store.GetAsync(account.CredentialId))!;
        // Simulate pre-provenance storage, with the original legacy client still known to the operator.
        await f.Store.TryRotateOAuthSecretAsync(saved.CredentialId, saved.Revision, saved.SecretRevision, f.Protector.Protect(saved.CredentialId, 1, "legacy-refresh"));
        saved = (await f.Store.GetAsync(saved.CredentialId))!;
        var config = new Dictionary<string, string?> { ["GoogleOAuth:ClientId"] = "new-client", ["GoogleOAuth:ClientSecret"] = "new-secret", ["GoogleOAuth:PublicAppUrl"] = "https://app.example.test" };
        if (retainLegacy) { config["GoogleEmailOAuth:ClientId"] = "client"; config["GoogleEmailOAuth:ClientSecret"] = "known-client-secret"; config["GoogleEmailOAuth:PublicAppUrl"] = "https://app.example.test"; }
        var options = new GoogleOAuthConfiguration(new ConfigurationBuilder().AddInMemoryCollection(config).Build()).Email;
        var service = new GoogleEmailOAuthService(f.Store, f.Protector, f.Transport, options, f.Time, false, f.Verifier);
        if (retainLegacy)
        {
            Assert.Equal("known-access", await service.AccessTokenAsync(saved.CredentialId, saved.Revision, default)); Assert.Equal(1, f.Transport.Refreshes);
            Assert.Contains("client_id=client&", f.Transport.LastRefreshBody); Assert.DoesNotContain("new-client", f.Transport.LastRefreshBody);
            var refreshed = (await f.Store.GetAsync(saved.CredentialId))!; var grant = GoogleOAuthRefreshGrant.Read(f.Protector.Unprotect(saved.CredentialId, 1, refreshed.ProtectedPayload));
            Assert.Equal("client", grant.ClientId); Assert.Equal("rotated-refresh", grant.Token); Assert.Equal(saved.Revision, refreshed.Revision);
        }
        else
        {
            Assert.Equal("email_oauth_client_changed", (await Assert.ThrowsAsync<AgentCoreException>(() => service.AccessTokenAsync(saved.CredentialId, saved.Revision, default).AsTask())).Code);
            Assert.Equal(0, f.Transport.Refreshes); var retained = (await f.Store.GetAsync(saved.CredentialId))!;
            Assert.Equal(saved.ProtectedPayload, retained.ProtectedPayload); Assert.Equal(saved.EmailAccount!.AuthEpoch, retained.EmailAccount!.AuthEpoch);
        }
    }
}
