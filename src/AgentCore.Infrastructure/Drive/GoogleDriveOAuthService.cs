using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;

namespace AgentCore.Infrastructure.Drive;

public sealed class GoogleDriveOptions
{
    public bool Enabled { get; set; } = true;
    public const int MaxTransferBytes = 50 * 1024 * 1024;
}
public sealed class GoogleDriveOAuthOptions : GoogleOAuthClientOptions
{
    public const string CallbackPath = "/api/v2/integrations/google-drive/oauth/callback";
}
public sealed class GoogleDriveOAuthService(ICredentialStore credentials, ICredentialProtector protector, IHttpClientFactory clients,
    GoogleDriveOptions host, GoogleDriveOAuthOptions options, TimeProvider time, bool synthetic) : IGoogleDriveOAuthService
{
    public const string HttpClientName = "GoogleDriveOAuth";
    private sealed record Attempt(Guid AccountId, Guid Owner, long Revision, long Epoch, string CookieHash, string Verifier, string Nonce, DateTimeOffset Expires, GoogleOAuthClient? Client);
    private sealed record Access(long Revision, long Epoch, string Token, DateTimeOffset Expires);
    private sealed record Token(string AccessToken, string? RefreshToken, string? IdToken, string[] Scopes, int ExpiresSeconds);
    internal Func<string, string, string, CancellationToken, Task<GoogleOAuthIdentity>> IdentityProof { get; init; } = GoogleOAuthProof.VerifyIdentity;
    private readonly object pendingGate = new(); private readonly Dictionary<string, Attempt> pending = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshGates = new(); private readonly ConcurrentDictionary<Guid, Access> cache = new();
    private readonly ConcurrentDictionary<Guid, DriveOAuthConnection> observations = new();
    public bool IsConfigured => host.Enabled && (synthetic || options.Configured);
    public bool IsSynthetic => synthetic;
    public string ReturnUrl => options.AppUri is { } uri ? new Uri(uri, "/admin/connections/credentials").AbsoluteUri : "/admin/connections/credentials";
    private static AgentCoreException Failure(string code, string message, int status = 400) => new(code, message, status);
    private void RequireHost() { if (!host.Enabled) throw Failure("drive_host_disabled", "Google Drive is disabled on this host. Ask the operator to enable GoogleDrive:Enabled.", 403); }
    private async ValueTask<Credential> Account(Guid id, long? revision, bool active, CancellationToken ct)
    {
        var c = await credentials.GetAsync(id, ct);
        if (c is not { Purpose: CredentialPurpose.GoogleDriveAccount, Kind: CredentialKind.Token, DriveAccount: not null }) throw Failure("drive_not_connected", "Google Drive Account was not found.", 404);
        if (revision is not null && c.Revision != revision) throw AgentCoreErrors.Conflict("Drive account changed. Reload before retrying.");
        if (active && c.Status != CredentialStatus.Active) throw Failure("drive_account_disabled", "Enable this Drive account before using it.", 403);
        return c;
    }
    public async ValueTask<DriveOAuthStart> StartAsync(Guid id, long revision, Guid ownerId, CancellationToken ct)
    {
        RequireHost(); if (ownerId == Guid.Empty) throw AgentCoreErrors.Forbidden("Owner is required."); var c = await Account(id, revision, true, ct);
        if (!IsConfigured) throw Failure("drive_oauth_not_configured", "Ask the operator to configure GoogleOAuth:ClientId, ClientSecret and PublicAppUrl.");
        var state = GoogleOAuthProof.Random(); var cookie = GoogleOAuthProof.Random(); var pkce = GoogleOAuthProof.Random(); var nonce = GoogleOAuthProof.Random(); var expires = time.GetUtcNow().AddMinutes(5);
        lock (pendingGate)
        {
            foreach (var key in pending.Where(p => p.Value.Expires <= time.GetUtcNow() || p.Value.AccountId == id).Select(p => p.Key).ToArray()) pending.Remove(key);
            if (pending.Count >= 128) throw Failure("drive_oauth_busy", "Too many connection attempts. Retry shortly.", 429);
            pending.Add(GoogleOAuthProof.Hash(state), new(id, ownerId, revision, c.DriveAccount!.AuthEpoch, GoogleOAuthProof.Hash(cookie), pkce, nonce, expires, options.Client));
        }
        var url = synthetic ? "/api/v2/integrations/google-drive/oauth/synthetic?state=" + Uri.EscapeDataString(state) : "https://accounts.google.com/o/oauth2/v2/auth?" + Form(new()
        {
            ["client_id"] = options.ClientId!,
            ["redirect_uri"] = new Uri(options.AppUri!, GoogleDriveOAuthOptions.CallbackPath).AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(' ', new[] { "openid", "email", DriveAccountProfile.Scope }),
            ["include_granted_scopes"] = "false",
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = GoogleOAuthProof.Challenge(pkce),
            ["code_challenge_method"] = "S256"
        });
        return new(url, cookie, expires, options.AppUri?.Scheme == "https", synthetic);
    }
    public async ValueTask<Guid> CompleteAsync(string state, string? correlation, Guid ownerId, string? code, string? error, CancellationToken ct)
    {
        RequireHost(); if (string.IsNullOrEmpty(state) || state.Length > 128 || correlation is null || correlation.Length > 128) throw Failure("drive_oauth_state_invalid", "Connection attempt is invalid. Start again.");
        Attempt a;
        lock (pendingGate)
        {
            var key = GoogleOAuthProof.Hash(state);
            if (!pending.TryGetValue(key, out a!) || a.Owner != ownerId || a.Expires <= time.GetUtcNow() || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a.CookieHash), Convert.FromHexString(GoogleOAuthProof.Hash(correlation))))
                throw Failure("drive_oauth_state_invalid", "Connection attempt expired or belongs to another browser. Start again.");
            pending.Remove(key);
        }
        var c = await Account(a.AccountId, a.Revision, true, ct); var current = c.DriveAccount!;
        if (current.AuthEpoch != a.Epoch) throw Failure("drive_oauth_state_invalid", "Authorization changed. Start again.");
        if (error is not null) { observations[c.CredentialId] = Status(c, "Google authorization was cancelled. Connect again when ready."); return c.CredentialId; }
        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096) throw Failure("drive_oauth_state_invalid", "Authorization code is missing. Start again.");
        try
        {
            var token = synthetic ? new Token("synthetic-drive-access", "synthetic-drive-refresh", null, [DriveAccountProfile.Scope], 3600)
                : await Exchange(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = a.Verifier, ["redirect_uri"] = new Uri(a.Client!.AppUri, GoogleDriveOAuthOptions.CallbackPath).AbsoluteUri }, ct, a.Client);
            var identity = synthetic ? new GoogleOAuthIdentity("synthetic-drive-" + c.CredentialId.ToString("N"), current.EmailAddress == "" ? "drive-" + c.CredentialId.ToString("N")[..8] + "@example.test" : current.EmailAddress)
                : await IdentityProof(token.IdToken ?? "", a.Client!.ClientId, a.Nonce, ct);
            if (current.GoogleSubject is { } subject && subject != identity.Subject) throw Failure("drive_oauth_identity_mismatch", "Choose the original Google identity, or create a separate Drive Account.");
            if (token.Scopes.Any(scope => scope.StartsWith("https://www.googleapis.com/auth/drive", StringComparison.Ordinal) && scope != DriveAccountProfile.Scope)) throw Failure("drive_oauth_scope_missing", "Reconnect using drive.file only; broad Drive scopes are unsupported.");
            if (!token.Scopes.Contains(DriveAccountProfile.Scope)) throw Failure("drive_oauth_scope_missing", "Grant access to app-authorized Drive files, then connect again.");
            if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw Failure("drive_oauth_refresh_missing", "Google did not supply offline authorization. Reconnect with consent.");
            if (!synthetic) await VerifyDrive(identity.Email, token.AccessToken, ct);
            if (time.GetUtcNow() >= a.Expires) throw Failure("drive_oauth_state_invalid", "Connection attempt expired. Start again.");
            var profile = (current with { EmailAddress = identity.Email, GoogleSubject = identity.Subject, GrantedScopes = token.Scopes, ConnectionStatus = DriveConnectionStatus.Connected, AuthEpoch = current.AuthEpoch + 1, ConnectedAtUtc = time.GetUtcNow(), ReconnectRequiredAtUtc = null, Synthetic = synthetic }).Validate();
            await credentials.SaveAsync(c with { DriveAccount = profile, ProtectedPayload = protector.Protect(c.CredentialId, c.ProtectionVersion, synthetic ? token.RefreshToken : GoogleOAuthRefreshGrant.Write(token.RefreshToken, a.Client!.ClientId)), SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1, UpdatedAtUtc = time.GetUtcNow() }, c.Revision, ct, c.SecretRevision);
            cache.TryRemove(c.CredentialId, out _); observations.TryRemove(c.CredentialId, out _); return c.CredentialId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { var failure = ex as AgentCoreException ?? Failure("drive_oauth_temporary_failure", "Google connection could not be completed. Connect again.", 502); observations[c.CredentialId] = Status(c, failure.Message); throw failure; }
    }
    private DriveOAuthConnection Status(Credential c, string? error = null) => new(!host.Enabled ? "HostDisabled" : c.Status == CredentialStatus.Disabled ? "Disabled" : c.DriveAccount!.ConnectionStatus.ToString(), IsConfigured, synthetic, error);
    public async ValueTask<DriveOAuthConnection> ConnectionAsync(Guid id, CancellationToken ct)
    {
        var c = await Account(id, null, false, ct); var result = Status(c); lock (pendingGate) if (host.Enabled && c.Status == CredentialStatus.Active && pending.Values.Any(a => a.AccountId == id && a.Expires > time.GetUtcNow())) result = result with { Status = "Connecting" };
        return observations.TryGetValue(id, out var observation) ? result with { Error = observation.Error, LastCheckedAtUtc = observation.LastCheckedAtUtc, ProviderRevoked = observation.ProviderRevoked } : result;
    }
    internal async ValueTask<string> AccessTokenAsync(Guid id, long revision, CancellationToken ct, bool forceRefresh = false)
    {
        RequireHost(); var gate = refreshGates.GetOrAdd(id, _ => new(1)); await gate.WaitAsync(ct);
        try
        {
            var c = await Account(id, revision, true, ct); var profile = c.DriveAccount!;
            if (!profile.IsUsable || c.ProtectedPayload == "" || profile.Synthetic != synthetic) throw Failure("drive_not_connected", "Connect or reconnect this Google Drive Account in Admin → Credentials.", 403);
            if (!forceRefresh && cache.TryGetValue(id, out var access) && access.Revision == revision && access.Epoch == profile.AuthEpoch && access.Expires > time.GetUtcNow().AddSeconds(60)) return access.Token;
            if (synthetic) return "synthetic-drive-access";
            Token token;
            try
            {
                var grant = options.RefreshGrant(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload), "drive");
                token = await Exchange(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.Token }, ct, grant.Client);
                if (token.Scopes.Length > 0 && (!token.Scopes.Contains(DriveAccountProfile.Scope) || token.Scopes.Any(scope => scope.StartsWith("https://www.googleapis.com/auth/drive", StringComparison.Ordinal) && scope != DriveAccountProfile.Scope))) throw Failure("drive_oauth_scope_missing", "Drive permission was revoked. Reconnect.", 403);
            }
            catch (AgentCoreException ex) when (ex.Code is "drive_access_revoked" or "drive_oauth_scope_missing") { await RejectAuthorizationAsync(id, revision, ct); throw; }
            if (token.RefreshToken is { Length: > 0 } replacement && !await credentials.TryRotateOAuthSecretAsync(id, revision, c.SecretRevision, protector.Protect(id, c.ProtectionVersion, GoogleOAuthRefreshGrant.Write(replacement, options.RefreshGrant(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload), "drive").Client.ClientId)), ct)) throw Failure("drive_access_revoked", "Authorization changed during refresh. Read current account state.", 403);
            await Account(id, revision, true, ct); cache[id] = new(revision, profile.AuthEpoch, token.AccessToken, time.GetUtcNow().AddSeconds(token.ExpiresSeconds)); return token.AccessToken;
        }
        finally { gate.Release(); }
    }
    internal async ValueTask RejectAuthorizationAsync(Guid id, long revision, CancellationToken ct)
    {
        var c = await Account(id, revision, true, ct); var profile = c.DriveAccount!;
        await credentials.SaveAsync(c with { ProtectedPayload = "", SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1, DriveAccount = profile with { ConnectionStatus = DriveConnectionStatus.ReconnectRequired, AuthEpoch = profile.AuthEpoch + 1, ReconnectRequiredAtUtc = time.GetUtcNow() }, UpdatedAtUtc = time.GetUtcNow() }, revision, ct, c.SecretRevision); cache.TryRemove(id, out _);
    }
    public async ValueTask<DriveOAuthConnection> CheckAsync(Guid id, long revision, CancellationToken ct)
    {
        RequireHost(); var c = await Account(id, revision, true, ct);
        try { var access = await AccessTokenAsync(id, revision, ct, true); if (!synthetic) await VerifyDrive(c.DriveAccount!.EmailAddress, access, ct); observations[id] = Status(c) with { LastCheckedAtUtc = time.GetUtcNow() }; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (AgentCoreException ex)
        {
            if (ex.Code == "drive_access_revoked") { var current = await Account(id, null, false, ct); if (current.Revision == revision && current.DriveAccount!.IsUsable) await RejectAuthorizationAsync(id, revision, ct); }
            observations[id] = Status(c, ex.Message) with { LastCheckedAtUtc = time.GetUtcNow() };
        }
        return await ConnectionAsync(id, ct);
    }
    public async ValueTask<DriveOAuthConnection> DisconnectAsync(Guid id, long revision, CancellationToken ct)
    {
        var c = await Account(id, revision, false, ct); var profile = c.DriveAccount!;
        await credentials.SaveAsync(c with { ProtectedPayload = "", SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1, DriveAccount = profile with { ConnectionStatus = DriveConnectionStatus.Disconnected, AuthEpoch = profile.AuthEpoch + 1 }, UpdatedAtUtc = time.GetUtcNow() }, revision, ct, c.SecretRevision);
        cache.TryRemove(id, out _); lock (pendingGate) foreach (var key in pending.Where(a => a.Value.AccountId == id).Select(a => a.Key).ToArray()) pending.Remove(key);
        var revoked = synthetic || c.ProtectedPayload == "";
        if (!revoked && options.Configured) try { using var client = clients.CreateClient(HttpClientName); using var response = await client.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = GoogleOAuthRefreshGrant.Read(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload)).Token }), ct); revoked = response.IsSuccessStatusCode; } catch (Exception) {/* local disconnect remains committed */}
        observations[id] = new("Disconnected", IsConfigured, synthetic, revoked ? null : "Disconnected locally. Google revocation could not be confirmed; remove app access in your Google account.", ProviderRevoked: revoked);
        return await ConnectionAsync(id, CancellationToken.None);
    }
    private async Task<Token> Exchange(Dictionary<string, string> fields, CancellationToken ct, GoogleOAuthClient? selectedClient = null)
    {
        var clientSettings = selectedClient ?? options.Client ?? throw new InvalidOperationException("Google OAuth client is unavailable.");
        fields["client_id"] = clientSettings.ClientId; fields["client_secret"] = clientSettings.ClientSecret; using var client = clients.CreateClient(HttpClientName);
        using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(fields), ct); using var json = await ReadJson(response, 65536, ct); var root = json.RootElement;
        if (!response.IsSuccessStatusCode) throw root.TryGetProperty("error", out var error) && error.GetString() == "invalid_grant" ? Failure("drive_access_revoked", "Google authorization was revoked. Reconnect.", 403) : Failure("drive_oauth_temporary_failure", "Google authorization is temporarily unavailable. Retry shortly.", 502);
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String || access.GetString() is not { Length: > 0 and <= 8192 } value) throw Failure("drive_oauth_response_invalid", "Google authorization response was invalid.", 502);
        return new(value, root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null, root.TryGetProperty("id_token", out var id) ? id.GetString() : null, root.TryGetProperty("scope", out var scope) ? (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : [], root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 60, 86400) : 3600);
    }
    private async Task VerifyDrive(string email, string token, CancellationToken ct)
    {
        using var client = clients.CreateClient("GoogleDrive"); using var request = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); using var json = await ReadJson(response, 65536, ct);
        if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("user", out var user) || !user.TryGetProperty("emailAddress", out var address) || !string.Equals(address.GetString(), email, StringComparison.OrdinalIgnoreCase)) throw Failure("drive_access_revoked", "Drive account access could not be verified. Reconnect.", 403);
    }
    internal static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw Failure("drive_provider_response_invalid", "Google response exceeded its limit.", 502);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await input.ReadAsync(buffer, ct); if (count == 0) break; if (output.Length + count > limit) throw Failure("drive_provider_response_invalid", "Google response exceeded its limit.", 502); output.Write(buffer, 0, count); }
        return JsonDocument.Parse(output.ToArray());
    }
    private static string Form(Dictionary<string, string> fields) => string.Join('&', fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
}
