using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantGoogleOAuthProvider
{
    public async ValueTask<CredentialOAuthCheck> CheckAsync(Credential current, CancellationToken ct)
    {
        var descriptor = Describe(current.Purpose);
        var savedSynthetic = current.EmailAccount?.Synthetic ?? current.DriveAccount?.Synthetic ?? false;
        if (!descriptor.Configured || !descriptor.HostEnabled || savedSynthetic != synthetic) throw CheckFailure();
        if (synthetic) return new();
        try
        {
            var client = configuration.Tenant!;
            var grant = GoogleOAuthRefreshGrant.Read(credentials.Unprotect(current.CredentialId, current.ProtectionVersion, current.ProtectedPayload));
            if (grant.ClientId != client.ClientId) throw ReconnectFailure();
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.Token,
                    ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret }) };
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            using var body = await ReadCheckBody(response, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Only Google's explicit permanent grant rejection changes local authorization.
                if (response.StatusCode == HttpStatusCode.BadRequest && body.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String && error.GetString() == "invalid_grant") return new(ReconnectRequired: true);
                throw CheckFailure();
            }
            var token = body.RootElement;
            var access = Field(token, "access_token", 16384);
            if (!string.Equals(Field(token, "token_type", 32), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !token.TryGetProperty("expires_in", out var expiry) || !expiry.TryGetInt32(out var seconds) || seconds is < 1 or > 86400) throw CheckFailure();
            if (token.TryGetProperty("scope", out var scope))
            {
                if (scope.ValueKind != JsonValueKind.String || scope.GetString() is not { Length: > 0 and <= 2048 } text) throw CheckFailure();
                var scopes = TenantCredentialOAuthRules.Scopes(current.Purpose, text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))!;
                if (current.Purpose == CredentialPurpose.EmailAccount ? !scopes.Contains(EmailAccountProfile.ReadScope) || !scopes.Contains(EmailAccountProfile.SendScope) : !scopes.Contains(DriveAccountProfile.Scope)) return new(ReconnectRequired: true);
            }
            using var profile = new HttpRequestMessage(HttpMethod.Get, current.Purpose == CredentialPurpose.EmailAccount
                ? "https://gmail.googleapis.com/gmail/v1/users/me/profile" : "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)");
            profile.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            using var account = await Json(profile, ct).ConfigureAwait(false);
            var email = current.Purpose == CredentialPurpose.EmailAccount ? Field(account.RootElement, "emailAddress", 254) : Field(account.RootElement.GetProperty("user"), "emailAddress", 254);
            if (!string.Equals(email, current.EmailAccount?.EmailAddress ?? current.DriveAccount?.EmailAddress, StringComparison.OrdinalIgnoreCase)) return new(ReconnectRequired: true);
            string? replacement = null;
            if (token.TryGetProperty("refresh_token", out _)) replacement = credentials.Protect(current.CredentialId, current.ProtectionVersion, GoogleOAuthRefreshGrant.Write(Field(token, "refresh_token", 8192), client.ClientId));
            if (Describe(current.Purpose) != descriptor) throw CheckFailure();
            return new(replacement);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AgentCoreException ex) when (ex.Code == "google_oauth_grant_invalid") { throw ReconnectFailure(); }
        catch (AgentCoreException ex) when (ex.Code.StartsWith("oauth_provider_", StringComparison.Ordinal)) { throw CheckFailure(); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException
            or KeyNotFoundException or FormatException or System.Security.Cryptography.CryptographicException or OperationCanceledException) { throw CheckFailure(); }
    }
    private static AgentCoreException ReconnectFailure() => new("oauth_reconnect_required", "The saved Google client binding cannot be proven. Explicitly reconnect this account using the current Google setup. The saved grant was retained.", 409);
    private static AgentCoreException CheckFailure() => new("oauth_check_unavailable", "Google access could not be checked. The saved connection was retained. Try again.", 503);
    private static async Task<JsonDocument> ReadCheckBody(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 65536) throw CheckFailure();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > 65536) throw CheckFailure();
            memory.Write(buffer, 0, read);
        }
        return JsonDocument.Parse(memory.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }
}
