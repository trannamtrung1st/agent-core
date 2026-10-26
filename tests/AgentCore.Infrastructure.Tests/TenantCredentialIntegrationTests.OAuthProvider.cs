using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using Microsoft.Extensions.Configuration;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, CredentialPurpose.EmailAccount, true)]
    [InlineData(true, CredentialPurpose.EmailAccount, true)]
    [InlineData(false, CredentialPurpose.GoogleDriveAccount, true)]
    [InlineData(true, CredentialPurpose.GoogleDriveAccount, true)]
    [InlineData(false, CredentialPurpose.EmailAccount, false)]
    [InlineData(true, CredentialPurpose.GoogleDriveAccount, false)]
    public async Task OAuth_provider_uses_pinned_shared_client_PKCE_nonce_exact_scopes_and_protected_refresh_grants(bool sqlite, CredentialPurpose purpose, bool revokeSucceeds)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(purpose), 1);
        var id = created.Credential.CredentialId;
        Dictionary<string, string> query = null!; var calls = 0;
        var http = new OAuthHttp(async request =>
        {
            calls++;
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
                var form = OAuthQuery(await request.Content!.ReadAsStringAsync());
                Assert.Equal("private-code", form["code"]); Assert.Equal("tenant-client", form["client_id"]); Assert.Equal("private-client-secret", form["client_secret"]);
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                Assert.Equal(query["code_challenge"], challenge); Assert.Equal(query["redirect_uri"], form["redirect_uri"]);
                return OAuthJson(new { access_token = "private-access", refresh_token = "private-refresh", id_token = "private-id", token_type = "Bearer",
                    scope = string.Join(' ', purpose == CredentialPurpose.EmailAccount ? new[] { "openid", "email", EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope } : new[] { "openid", "email", DriveAccountProfile.Scope }) });
            }
            if (request.RequestUri.AbsolutePath == "/revoke")
            {
                Assert.Equal(HttpMethod.Post, request.Method); Assert.Empty(request.RequestUri.Query);
                Assert.Equal("private-refresh", OAuthQuery(await request.Content!.ReadAsStringAsync())["token"]);
                return new(revokeSucceeds ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable) { Content = new StringContent("private-revocation-body") };
            }
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("private-access", request.Headers.Authorization!.Parameter);
            Assert.Equal(purpose == CredentialPurpose.EmailAccount ? "https://gmail.googleapis.com/gmail/v1/users/me/profile" : "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)", request.RequestUri.AbsoluteUri);
            return purpose == CredentialPurpose.EmailAccount ? OAuthJson(new { emailAddress = "verified@example.test" }) : OAuthJson(new { user = new { emailAddress = "verified@example.test" } });
        });
        var provider = WithIdentity();
        TenantGoogleOAuthProvider WithIdentity() => new(SharedOAuth(), new(), new(Path.Combine(f.Root, "keys")), f.Protector, http, false)
        {
            IdentityProof = (token, client, nonce, _) =>
            {
                Assert.Equal("private-id", token); Assert.Equal("tenant-client", client); Assert.Equal(query["nonce"], nonce);
                return Task.FromResult(new GoogleOAuthIdentity("verified-subject", "verified@example.test"));
            }
        };
        var service = OAuthService(f, provider);
        var start = await service.StartAsync(owner.TenantId, id, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId), 2, 1);
        var authorization = new Uri(start.AuthorizationUrl); Assert.Equal("accounts.google.com", authorization.Host);
        query = OAuthQuery(authorization.Query.TrimStart('?'));
        Assert.Equal("false", query["include_granted_scopes"]); Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("https://app.example.test/api/v2/integrations/tenant-google/oauth/callback", query["redirect_uri"]);
        Assert.DoesNotContain("private-client-secret", start.AuthorizationUrl); Assert.DoesNotContain("legacy", start.AuthorizationUrl);
        Assert.True(TenantCredentialOAuthService.TryState(query["state"], out _, out var attemptId));
        var pending = (await ((ITenantCredentialOAuthStore)f.Iam).ReadCredentialOAuthAttemptAsync(attemptId))!;
        Assert.DoesNotContain(query["nonce"], pending.ProtectedProof);
        var reopenedProof = new TenantOAuthProofProtector(Path.Combine(f.Root, "keys"));
        Assert.Equal(query["nonce"], reopenedProof.Unprotect(attemptId, pending.ProtectedProof).Nonce);
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() => Task.Run(() => reopenedProof.Unprotect(Guid.NewGuid(), pending.ProtectedProof)));
        var connected = await service.CompleteAsync(query["state"], start.Correlation, null, "private-code", null, false);
        Assert.Equal("connected", connected.Outcome); Assert.Equal(2, calls);
        var value = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        var grant = GoogleOAuthRefreshGrant.Read(f.Protector.Unprotect(id, 1, value.ProtectedPayload));
        Assert.Equal("tenant-client", grant.ClientId); Assert.Equal("private-refresh", grant.Token);
        Assert.False(value.EmailAccount?.Synthetic ?? value.DriveAccount!.Synthetic);
        var disconnected = await service.DisconnectAsync(owner.TenantId, id, new(owner.AccountId, session.SessionId, owner.OwnerPrincipalId), 4, 2);
        Assert.Equal(revokeSucceeds, disconnected.ProviderRevoked); Assert.Equal(3, calls);
        Assert.Empty((await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value.ProtectedPayload);
        AssertSafe(await f.Service.ReadAsync(owner.OwnerPrincipalId, owner.TenantId, id), "private-code", "private-access", "private-refresh", "private-id", pending.ProtectedProof);
    }

    [Theory]
    [InlineData("mixed-scope")]
    [InlineData("missing-refresh")]
    [InlineData("wrong-profile")]
    [InlineData("provider-error")]
    [InlineData("oversize")]
    [InlineData("bad-nonce")]
    [InlineData("malformed-proof")]
    public async Task OAuth_provider_failures_are_bounded_one_use_and_preserve_the_prior_connection(string failure)
    {
        await using var f = await Fixture.Create(true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var session = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var created = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, OAuthInput(CredentialPurpose.GoogleDriveAccount), 1);
        var id = created.Credential.CredentialId; var fail = false; var posts = 0;
        var http = new OAuthHttp(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posts++;
                if (fail && failure == "provider-error") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("private-provider-error") });
                if (fail && failure == "oversize") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 65537)) });
                return Task.FromResult(OAuthJson(new { access_token = "private-access", refresh_token = fail && failure == "missing-refresh" ? null : "private-refresh", id_token = "private-id", token_type = "Bearer",
                    scope = fail && failure == "mixed-scope" ? "openid email https://www.googleapis.com/auth/drive" : "openid email " + DriveAccountProfile.Scope }));
            }
            return Task.FromResult(OAuthJson(new { user = new { emailAddress = fail && failure == "wrong-profile" ? "other@example.test" : "verified@example.test" } }));
        });
        var provider = new TenantGoogleOAuthProvider(SharedOAuth(), new(), new(Path.Combine(f.Root, "keys")), f.Protector, http, false)
        {
            IdentityProof = (_, _, _, _) => fail && failure == "bad-nonce" ? throw new AccessDeniedException("invalid_nonce")
                : fail && failure == "malformed-proof" ? throw new FormatException("private-id-body") : Task.FromResult(new GoogleOAuthIdentity("verified-subject", "verified@example.test"))
        };
        var service = OAuthService(f, provider); var initiator = new CredentialOAuthInitiator(owner.AccountId, session.SessionId, owner.OwnerPrincipalId);
        var first = await service.StartAsync(owner.TenantId, id, initiator, 2, 1); var query = OAuthQuery(new Uri(first.AuthorizationUrl).Query.TrimStart('?'));
        Assert.Equal("connected", (await service.CompleteAsync(query["state"], first.Correlation, null, "private-code", null, false)).Outcome);
        var before = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        fail = true; var reconnect = await service.StartAsync(owner.TenantId, id, initiator, 4, 2); query = OAuthQuery(new Uri(reconnect.AuthorizationUrl).Query.TrimStart('?'));
        var result = await service.CompleteAsync(query["state"], reconnect.Correlation, null, "private-reconnect-code", null, false);
        Assert.Equal("failed", result.Outcome); Assert.Equal(2, posts);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.CompleteAsync(query["state"], reconnect.Correlation, null, "private-reconnect-code", null, false).AsTask());
        Assert.Equal(2, posts);
        var retained = (await f.Credentials.FindTenantCredentialAsync(owner.TenantId, id))!.Value;
        Assert.Equal(before.ProtectedPayload, retained.ProtectedPayload); Assert.Equal(before.Revision, retained.Revision); Assert.Equal(before.SecretRevision, retained.SecretRevision);
        AssertSafe(result, "private-reconnect-code", "private-access", "private-refresh", "private-id", "private-provider-error");
        TenantCredentialOAuthService.TryState(query["state"], out _, out var attempt);
        Assert.Equal(CredentialOAuthOutcome.Failed, (await ((ITenantCredentialOAuthStore)f.Iam).ReadCredentialOAuthAttemptAsync(attempt))!.Outcome);
    }

    private static GoogleOAuthConfiguration SharedOAuth() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["GoogleOAuth:ClientId"] = "tenant-client", ["GoogleOAuth:ClientSecret"] = "private-client-secret", ["GoogleOAuth:PublicAppUrl"] = "https://app.example.test",
    }).Build());
    private static Dictionary<string, string> OAuthQuery(string query) => query.Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static HttpResponseMessage OAuthJson(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class OAuthHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler, IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(this, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
