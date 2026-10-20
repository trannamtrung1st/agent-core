using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantGoogleOAuthProvider
{
    // Internal transport callback only. No token crosses an Application port or response.
    internal async ValueTask<string> RuntimeTokenAsync(Func<CancellationToken, ValueTask<Credential>> recheck,
        Func<CredentialOAuthCheck, CancellationToken, ValueTask> maintain, CancellationToken ct)
    {
        var current = await recheck(ct).ConfigureAwait(false); var descriptor = Describe(current.Purpose);
        if (synthetic || !descriptor.Configured || !descriptor.HostEnabled || current.EmailAccount?.Synthetic == true || current.DriveAccount?.Synthetic == true)
            throw RuntimeFailure("oauth_provider_unavailable");
        var client = configuration.Tenant!;
        try
        {
            var grant = GoogleOAuthRefreshGrant.Read(credentials.Unprotect(current.CredentialId, current.ProtectionVersion, current.ProtectedPayload));
            if (grant.ClientId != descriptor.ClientId) throw RuntimeFailure("oauth_client_changed");
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.Token,
                    ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret }) };
            await recheck(ct).ConfigureAwait(false);
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            using var body = await ReadCheckBody(response, ct).ConfigureAwait(false);
            await recheck(ct).ConfigureAwait(false);
            if (Describe(current.Purpose) != descriptor) throw RuntimeFailure("oauth_client_changed");
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.BadRequest && body.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String && error.GetString() == "invalid_grant")
                { await maintain(new(ReconnectRequired: true), ct).ConfigureAwait(false); throw RuntimeFailure("oauth_reconnect_required"); }
                throw RuntimeFailure("oauth_provider_unavailable");
            }
            var token = body.RootElement; var access = Field(token, "access_token", 16384);
            if (!string.Equals(Field(token, "token_type", 32), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !token.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds is < 1 or > 86400)
                throw RuntimeFailure("oauth_provider_invalid");
            if (token.TryGetProperty("scope", out var scope))
            {
                if (scope.ValueKind != JsonValueKind.String || scope.GetString() is not { Length: > 0 and <= 2048 } text) throw RuntimeFailure("oauth_provider_invalid");
                var scopes = TenantCredentialOAuthRules.Scopes(current.Purpose, text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))!;
                if (current.Purpose == CredentialPurpose.EmailAccount ? !scopes.Contains(EmailAccountProfile.ReadScope) : !scopes.Contains(DriveAccountProfile.Scope))
                { await maintain(new(ReconnectRequired: true), ct).ConfigureAwait(false); throw RuntimeFailure("oauth_reconnect_required"); }
            }
            if (token.TryGetProperty("refresh_token", out _))
                await maintain(new(credentials.Protect(current.CredentialId, current.ProtectionVersion,
                    GoogleOAuthRefreshGrant.Write(Field(token, "refresh_token", 8192), descriptor.ClientId))), ct).ConfigureAwait(false);
            await recheck(ct).ConfigureAwait(false); return access;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException
            or FormatException or System.Security.Cryptography.CryptographicException or OperationCanceledException)
        { throw RuntimeFailure("oauth_provider_unavailable"); }
    }
    private static AgentCoreException RuntimeFailure(string code) => new(code,
        "The connected account could not be read. Check its connection and current access before retrying the read.", code == "oauth_reconnect_required" ? 403 : 503);
}
