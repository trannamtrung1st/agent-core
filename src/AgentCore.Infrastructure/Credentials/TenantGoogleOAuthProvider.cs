using System.Net.Http.Headers;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Drive;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantGoogleOAuthProvider(GoogleOAuthConfiguration configuration, GoogleDriveOptions driveHost, TenantOAuthProofProtector proofs,
    ICredentialProtector credentials, IHttpClientFactory clients, bool synthetic) : ITenantCredentialOAuthProvider, ITenantCredentialOAuthVerifier
{
    public const string HttpClientName = "TenantGoogleOAuth";
    public const string Root = "/api/v2/integrations/tenant-google/oauth";
    internal Func<string, string, string, CancellationToken, Task<GoogleOAuthIdentity>> IdentityProof { get; init; } = GoogleOAuthProof.VerifyIdentity;
    public CredentialOAuthDescriptor Describe(CredentialPurpose purpose)
    {
        if (purpose is not (CredentialPurpose.EmailAccount or CredentialPurpose.GoogleDriveAccount)) throw AgentCoreErrors.Validation("Unsupported OAuth purpose.");
        var client = configuration.Tenant;
        return new(synthetic ? "agent-core.tenant.synthetic" : client?.ClientId ?? "", synthetic ? "http://localhost" + Root + "/callback"
            : client is null ? "" : new Uri(client.AppUri, Root + "/callback").AbsoluteUri, synthetic, synthetic || client is not null,
            purpose != CredentialPurpose.GoogleDriveAccount || driveHost.Enabled);
    }
    public CredentialOAuthPrepared Prepare(CredentialOAuthAttempt attempt, string state)
    {
        Match(attempt); var proof = new TenantOAuthProof(GoogleOAuthProof.Random(), GoogleOAuthProof.Random());
        var payload = proofs.Protect(attempt.AttemptId, proof);
        if (synthetic) return new(Root + "/synthetic?state=" + Uri.EscapeDataString(state), payload);
        var fields = new Dictionary<string, string>
        {
            ["client_id"] = attempt.Provider.ClientId, ["redirect_uri"] = attempt.Provider.RedirectUri, ["response_type"] = "code",
            ["scope"] = string.Join(' ', RequestedScopes(attempt.Purpose)), ["state"] = state, ["access_type"] = "offline", ["prompt"] = "consent select_account",
            ["include_granted_scopes"] = "false", ["code_challenge"] = GoogleOAuthProof.Challenge(proof.Verifier), ["code_challenge_method"] = "S256", ["nonce"] = proof.Nonce
        };
        return new("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))), payload);
    }
    public async ValueTask<CredentialOAuthGrant> ExchangeAsync(CredentialOAuthClaim claim, string code, Credential current, CancellationToken ct)
    {
        var a = claim.Attempt; Match(a); var proof = proofs.Unprotect(a.AttemptId, claim.ProtectedProof);
        if (synthetic)
        {
            if (code != "synthetic-consent") throw Failure("oauth_provider_rejected");
            var email = current.EmailAccount?.EmailAddress ?? current.DriveAccount!.EmailAddress;
            if (email.Length == 0) email = "synthetic-" + current.CredentialId.ToString("N") + "@example.test";
            return Grant(a, "synthetic-" + current.CredentialId.ToString("N"), email, RequestedScopes(a.Purpose), "synthetic-tenant-refresh-" + current.CredentialId.ToString("N"));
        }
        try
        {
            var client = configuration.Tenant!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code,
                ["code_verifier"] = proof.Verifier, ["redirect_uri"] = a.Provider.RedirectUri, ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret }) };
            using var body = await Json(request, ct).ConfigureAwait(false); var token = body.RootElement;
            var access = Field(token, "access_token", 16384); var refresh = Field(token, "refresh_token", 8192); var idToken = Field(token, "id_token", 32768);
            if (!string.Equals(Field(token, "token_type", 32), "Bearer", StringComparison.OrdinalIgnoreCase)) throw Failure("oauth_provider_invalid");
            var scopes = Field(token, "scope", 2048).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var normalized = TenantCredentialOAuthRules.Scopes(a.Purpose, scopes)!;
            if (a.Purpose == CredentialPurpose.EmailAccount ? !normalized.Contains(EmailAccountProfile.ReadScope) || !normalized.Contains(EmailAccountProfile.SendScope)
                : !normalized.Contains(DriveAccountProfile.Scope)) throw Failure("oauth_scope_missing");
            var who = await IdentityProof(idToken, client.ClientId, proof.Nonce, ct).ConfigureAwait(false);
            using var profile = new HttpRequestMessage(HttpMethod.Get, a.Purpose == CredentialPurpose.EmailAccount
                ? "https://gmail.googleapis.com/gmail/v1/users/me/profile" : "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
            profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var account = await Json(profile, ct).ConfigureAwait(false);
            var actualEmail = a.Purpose == CredentialPurpose.EmailAccount ? Field(account.RootElement, "emailAddress", 254)
                : Field(account.RootElement.GetProperty("user"), "emailAddress", 254);
            if (!string.Equals(who.Email, actualEmail, StringComparison.OrdinalIgnoreCase)) throw Failure("oauth_identity_mismatch");
            Match(a); return Grant(a, who.Subject, who.Email, normalized, refresh);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or FormatException or System.Security.Cryptography.CryptographicException or Google.Apis.Auth.InvalidJwtException or OperationCanceledException)
        { throw Failure("oauth_provider_unavailable", 502); }
    }
    public async ValueTask<bool> RevokeAsync(Credential current, CancellationToken ct)
    {
        var wasSynthetic = current.EmailAccount?.Synthetic ?? current.DriveAccount?.Synthetic ?? false;
        if (synthetic || wasSynthetic) return wasSynthetic;
        try
        {
            var client = configuration.Tenant;
            if (client is null) return false;
            var grant = GoogleOAuthRefreshGrant.Read(credentials.Unprotect(current.CredentialId, current.ProtectionVersion, current.ProtectedPayload));
            if (grant.ClientId != client.ClientId) return false;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/revoke")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = grant.Token }) };
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is AgentCoreException or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or ArgumentException) { return false; }
    }
    private CredentialOAuthGrant Grant(CredentialOAuthAttempt a, string subject, string email, IReadOnlyList<string> scopes, string refresh) => new(subject, email, scopes,
        credentials.Protect(a.CredentialId, 1, GoogleOAuthRefreshGrant.Write(refresh, a.Provider.ClientId)), synthetic);
    private void Match(CredentialOAuthAttempt attempt)
    {
        var current = Describe(attempt.Purpose);
        if (!current.Configured || !current.HostEnabled || current != attempt.Provider) throw Failure("oauth_client_changed", 409);
    }
    private static string[] RequestedScopes(CredentialPurpose purpose) => purpose == CredentialPurpose.EmailAccount
        ? ["openid", "email", EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope] : ["openid", "email", DriveAccountProfile.Scope];
    private static string Field(JsonElement body, string name, int max)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text
            || text.Length > max || text.Any(char.IsControl)) throw Failure("oauth_provider_invalid");
        return text;
    }
    private async Task<JsonDocument> Json(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536) throw Failure("oauth_provider_rejected", 502);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > 65536) throw Failure("oauth_provider_invalid", 502);
            memory.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
    private static AgentCoreException Failure(string code, int status = 400) => new(code, "Google authorization could not be completed. The previous connection was retained. Connect again.", status);
}
