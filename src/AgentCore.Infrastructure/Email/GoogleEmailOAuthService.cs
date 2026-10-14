using AgentCore.Infrastructure.Credentials;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using Google.Apis.Auth;
using AgentCore.Application.Observability;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Email;

public sealed class GoogleEmailOAuthOptions : GoogleOAuthClientOptions
{
    public const string CallbackPath = "/api/v2/integrations/google-email/oauth/callback";
}
internal sealed record GoogleEmailIdentity(string Subject, string Email);
internal interface IGoogleEmailIdentityVerifier
{
    Task<GoogleEmailIdentity> VerifyAsync(string token, string clientId, string nonce, CancellationToken ct);
}
internal sealed class GoogleEmailIdentityVerifier : IGoogleEmailIdentityVerifier
{
    public async Task<GoogleEmailIdentity> VerifyAsync(string token, string clientId, string nonce, CancellationToken ct)
    {
        try
        {
            var identity = await AgentCore.Infrastructure.Credentials.GoogleOAuthProof.VerifyIdentity(token, clientId, nonce, ct);
            return new(identity.Subject, identity.Email);
        }
        catch (AgentCoreException) { throw Failure("email_oauth_identity_mismatch", "Google identity validation failed. Connect again."); }
    }
    private static AgentCoreException Failure(string code, string message) => new(code, message, 400);
}

public sealed class GoogleEmailOAuthService : IEmailOAuthService
{
    public const string HttpClientName = "GoogleEmailOAuth";
    private readonly ICredentialStore credentials;
    private readonly ICredentialProtector protector;
    private readonly IHttpClientFactory clients;
    private readonly GoogleEmailOAuthOptions options;
    private readonly TimeProvider time;
    private readonly bool synthetic;
    private readonly IGoogleEmailIdentityVerifier verifier;
    private readonly ILogger<GoogleEmailOAuthService>? logger;
    private readonly IDiagnosticIdSource? diagnosticIds;
    private readonly object pendingGate = new();
    private readonly Dictionary<string, Attempt> pending = [];
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> refreshGates = new();
    private readonly ConcurrentDictionary<Guid, Access> cache = new();
    private readonly ConcurrentDictionary<Guid, EmailOAuthConnection> observations = new();
    private sealed record Attempt(Guid AccountId, Guid OwnerId, long Revision, long Epoch, string CookieHash, string Verifier, string Nonce, DateTimeOffset Expires, GoogleOAuthClient? Client);
    private sealed record Access(long Revision, long Epoch, string Token, DateTimeOffset Expires);
    private sealed record Token(string AccessToken, string? RefreshToken, string? IdToken, string[] Scopes, int ExpiresSeconds);
    public GoogleEmailOAuthService(ICredentialStore credentials, ICredentialProtector protector, IHttpClientFactory clients,
        GoogleEmailOAuthOptions options, TimeProvider time, bool synthetic, ILogger<GoogleEmailOAuthService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
        : this(credentials, protector, clients, options, time, synthetic, new GoogleEmailIdentityVerifier(), logger, diagnosticIds) { }
    internal GoogleEmailOAuthService(ICredentialStore credentials, ICredentialProtector protector, IHttpClientFactory clients,
        GoogleEmailOAuthOptions options, TimeProvider time, bool synthetic, IGoogleEmailIdentityVerifier verifier, ILogger<GoogleEmailOAuthService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
    { this.credentials = credentials; this.protector = protector; this.clients = clients; this.options = options; this.time = time; this.synthetic = synthetic; this.verifier = verifier; this.logger = logger; this.diagnosticIds = diagnosticIds; }
    public bool IsConfigured => synthetic || options.Configured;
    public bool IsSynthetic => synthetic;
    public string ReturnUrl => options.AppUri is { } uri ? new Uri(uri, "/admin/connections/credentials").AbsoluteUri : "/admin/connections/credentials";
    private static string Random() => GmailMime.EncodeBase64Url(RandomNumberGenerator.GetBytes(32));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static AgentCoreException Failure(string code, string message, int status = 400) => new(code, message, status);
    private async ValueTask<Credential> Account(Guid id, long? revision, bool active, CancellationToken ct)
    {
        var c = await credentials.GetAsync(id, ct);
        if (c is not { Purpose: CredentialPurpose.EmailAccount, Kind: CredentialKind.Token, EmailAccount.AuthMethod: EmailAuthMethod.GoogleOAuth2 })
            throw Failure("email_account_unavailable", "Gmail OAuth account was not found.", 404);
        if (revision is not null && c.Revision != revision) throw AgentCoreErrors.Conflict("Email account changed. Refresh and retry.");
        if (active && c.Status != CredentialStatus.Active) throw Failure("email_account_disabled", "Enable this account before connecting or using it.", 403);
        return c;
    }
    public async ValueTask<EmailOAuthStart> StartAsync(Guid id, long revision, Guid ownerId, CancellationToken ct)
    {
        using var observation = new EmailOperationObservation("oauth.start", synthetic, time, id, ct);
        if (ownerId == Guid.Empty) throw AgentCoreErrors.Forbidden("Owner is required.");
        var c = await Account(id, revision, true, ct);
        if (!synthetic && !options.Configured) throw Failure("email_oauth_not_configured", "Google connection not configured. Ask the operator to configure GoogleOAuth:ClientId, ClientSecret and PublicAppUrl.");
        var state = Random(); var cookie = Random(); var pkce = Random(); var nonce = Random(); var expires = time.GetUtcNow().AddMinutes(5);
        lock (pendingGate)
        {
            foreach (var key in pending.Where(p => p.Value.Expires <= time.GetUtcNow() || p.Value.AccountId == id).Select(p => p.Key).ToArray()) pending.Remove(key);
            if (pending.Count >= 128) throw Failure("email_oauth_busy", "Too many connection attempts. Try again shortly.", 429);
            pending.Add(Hash(state), new(id, ownerId, revision, c.EmailAccount!.AuthEpoch, Hash(cookie), pkce, nonce, expires, options.Client));
        }
        var url = synthetic ? "/api/v2/integrations/google-email/oauth/synthetic?state=" + Uri.EscapeDataString(state)
            : "https://accounts.google.com/o/oauth2/v2/auth?" + Form(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId!, ["redirect_uri"] = new Uri(options.AppUri!, GoogleEmailOAuthOptions.CallbackPath).AbsoluteUri,
                ["response_type"] = "code", ["scope"] = string.Join(' ', new[] { "openid", "email", EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope }),
                ["include_granted_scopes"] = "false", ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state, ["nonce"] = nonce,
                ["code_challenge"] = GmailMime.EncodeBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(pkce))), ["code_challenge_method"] = "S256"
            });
        observation.Complete();
        return new(url, cookie, expires, options.AppUri?.Scheme == "https", synthetic);
    }
    public async ValueTask<Guid> CompleteAsync(string state, string? correlation, Guid ownerId, string? code, string? error, CancellationToken ct)
    {
        using var observation = new EmailOperationObservation("oauth.connect", synthetic, time, Guid.Empty, ct);
        if (string.IsNullOrEmpty(state) || state.Length > 128 || correlation is null || correlation.Length > 128) throw Failure("email_oauth_state_invalid", "Connection attempt is invalid. Start again.");
        Attempt a;
        lock (pendingGate)
        {
            var key = Hash(state);
            if (!pending.TryGetValue(key, out a!) || a.OwnerId != ownerId || a.Expires <= time.GetUtcNow()
                || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(a.CookieHash), Convert.FromHexString(Hash(correlation))))
                throw Failure("email_oauth_state_invalid", "Connection attempt expired or belongs to another browser. Start again.");
            pending.Remove(key); // Consume before exchange, including denial and failed exchange.
        }
        var c = await Account(a.AccountId, a.Revision, true, ct);
        observation.Correlate(c.CredentialId);
        if (c.EmailAccount!.AuthEpoch != a.Epoch) throw Failure("email_oauth_state_invalid", "Authorization changed. Start again.");
        if (error is not null) { observations[a.AccountId] = Status(c, "email_oauth_denied: Google authorization was cancelled. Connect again when ready."); observation.Complete("denied"); return a.AccountId; }
        if (string.IsNullOrWhiteSpace(code) || code.Length > 4096) throw Failure("email_oauth_state_invalid", "Authorization code is missing. Start again.");
        var phase = "exchange";
        try
        {
            var token = synthetic ? new Token("synthetic-access", "synthetic-refresh", null, [EmailAccountProfile.ReadScope, EmailAccountProfile.SendScope], 3600)
                : await Exchange(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["code_verifier"] = a.Verifier,
                    ["redirect_uri"] = new Uri(a.Client!.AppUri, GoogleEmailOAuthOptions.CallbackPath).AbsoluteUri }, c.CredentialId, "oauth.connect.exchange", ct, a.Client);
            phase = "identity";
            var identity = synthetic ? new GoogleEmailIdentity("synthetic-" + c.CredentialId.ToString("N"), c.EmailAccount.EmailAddress == "" ? "synthetic-" + c.CredentialId.ToString("N")[..8] + "@example.test" : c.EmailAccount.EmailAddress)
                : await verifier.VerifyAsync(token.IdToken ?? "", a.Client!.ClientId, a.Nonce, ct);
            if (c.EmailAccount.GoogleSubject is { } subject && subject != identity.Subject
                || c.EmailAccount.GoogleSubject is null && c.EmailAccount.EmailAddress != "" && !string.Equals(c.EmailAccount.EmailAddress, identity.Email, StringComparison.OrdinalIgnoreCase))
                throw Failure("email_oauth_identity_mismatch", "Choose the original Google account. Create a separate Email Account for another identity.");
            if (!token.Scopes.Contains(EmailAccountProfile.ReadScope) || !token.Scopes.Contains(EmailAccountProfile.SendScope))
                throw Failure("email_oauth_scope_missing", "Grant both mailbox read and send permissions, then connect again.");
            if (string.IsNullOrWhiteSpace(token.RefreshToken)) throw Failure("email_oauth_refresh_missing", "Google did not supply offline authorization. Reconnect with consent.");
            phase = "profile";
            if (!synthetic) await VerifyMailbox(identity.Email, token.AccessToken, c.CredentialId, "oauth.connect.profile", ct);
            var profile = c.EmailAccount with { EmailAddress = identity.Email, GoogleSubject = identity.Subject, GrantedScopes = token.Scopes,
                ConnectionStatus = EmailConnectionStatus.Connected, AuthEpoch = a.Epoch + 1, ConnectedAtUtc = time.GetUtcNow(), Synthetic = synthetic };
            profile.Validate();
            if (time.GetUtcNow() >= a.Expires) throw Failure("email_oauth_state_invalid", "Connection attempt expired. Start again.");
            phase = "persist";
            await credentials.SaveAsync(c with { EmailAccount = profile, ProtectedPayload = protector.Protect(c.CredentialId, c.ProtectionVersion, synthetic ? token.RefreshToken : GoogleOAuthRefreshGrant.Write(token.RefreshToken, a.Client!.ClientId)),
                SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1, UpdatedAtUtc = time.GetUtcNow() }, c.Revision, ct, c.SecretRevision);
            cache.TryRemove(c.CredentialId, out _); observations.TryRemove(c.CredentialId, out _);
            observation.Complete();
            return c.CredentialId;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { observation.Fail(ex); throw; }
        catch (Exception ex)
        {
            var failure = ex as AgentCoreException ?? (phase == "identity" && ex is not (HttpRequestException or OperationCanceledException)
                ? Failure("email_oauth_identity_mismatch", "Google identity validation failed. Connect the original account again.")
                : Failure("email_oauth_temporary_failure", "Google connection could not be completed. Try again.", 502));
            var diagnostic = failure.DiagnosticId ?? HandledFailureLog.Warning(logger, ex, "email.oauth.connect." + phase, failure.Code, new DiagnosticContext(ProviderAlias: "gmail", CredentialId: c.CredentialId), diagnostics: diagnosticIds);
            observations[c.CredentialId] = Status(c, failure.Code + ": " + failure.Message) with { DiagnosticId = diagnostic };
            observation.Fail(failure);
            return c.CredentialId;
        }
    }
    private EmailOAuthConnection Status(Credential c, string? error = null) => new(c.Status == CredentialStatus.Disabled ? "Disabled" : c.EmailAccount!.ConnectionStatus.ToString(), synthetic || options.Configured, synthetic, error);
    public async ValueTask<EmailOAuthConnection> ConnectionAsync(Guid id, CancellationToken ct)
    {
        var c = await Account(id, null, false, ct); var status = Status(c);
        lock (pendingGate) if (c.Status == CredentialStatus.Active && pending.Values.Any(a => a.AccountId == id && a.Expires > time.GetUtcNow())) status = status with { Status = "Connecting" };
        return observations.TryGetValue(id, out var observation) ? status with { Error = observation.Error, LastCheckedAtUtc = observation.LastCheckedAtUtc, ProviderRevoked = observation.ProviderRevoked, DiagnosticId = observation.DiagnosticId } : status;
    }
    internal async ValueTask<string> AccessTokenAsync(Guid id, long revision, CancellationToken ct, bool forceRefresh = false)
    {
        using var observation = new EmailOperationObservation("oauth.refresh", synthetic, time, id, ct);
        var gate = refreshGates.GetOrAdd(id, _ => new(1));
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException ex) { observation.Fail(ex); throw; }
        try
        {
            var c = await Account(id, revision, true, ct);
            if (c.EmailAccount is not { ConnectionStatus: EmailConnectionStatus.Connected } p || c.ProtectedPayload == "")
                throw Failure("email_oauth_reconnect_required", "Reconnect this Gmail account in Connections → Credentials.", 403);
            if (p.Synthetic != synthetic) throw Failure("email_oauth_reconnect_required", "This authorization belongs to another provider profile. Reconnect.", 403);
            if (!forceRefresh && cache.TryGetValue(id, out var access) && access.Revision == revision && access.Epoch == p.AuthEpoch && access.Expires > time.GetUtcNow().AddSeconds(60)) { observation.Complete("cached"); return access.Token; }
            if (synthetic) { observation.Complete(); return "synthetic-access"; }
            Token token;
            try
            {
                var grant = options.RefreshGrant(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload), "email");
                token = await Exchange(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = grant.Token }, id, "oauth.refresh", ct, grant.Client);
                if (token.Scopes.Length > 0 && (!token.Scopes.Contains(EmailAccountProfile.ReadScope) || !token.Scopes.Contains(EmailAccountProfile.SendScope)))
                    throw Failure("email_oauth_scope_missing", "Google authorization lacks required permissions. Reconnect.", 403);
            }
            catch (AgentCoreException ex) when (ex.Code is "email_oauth_reconnect_required" or "email_oauth_scope_missing")
            {
                cache.TryRemove(id, out _);
                await credentials.SaveAsync(c with { ProtectedPayload = "", SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1,
                    EmailAccount = p with { ConnectionStatus = EmailConnectionStatus.ReconnectRequired, AuthEpoch = p.AuthEpoch + 1 }, UpdatedAtUtc = time.GetUtcNow() }, c.Revision, ct, c.SecretRevision);
                throw;
            }
            if (token.RefreshToken is { Length: > 0 } replacement && !await credentials.TryRotateOAuthSecretAsync(id, revision, c.SecretRevision, protector.Protect(id, c.ProtectionVersion, GoogleOAuthRefreshGrant.Write(replacement, options.RefreshGrant(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload), "email").Client.ClientId)), ct))
                throw Failure("email_oauth_reconnect_required", "Authorization changed during refresh. Retry with current account state.", 403);
            // Also fence a disconnect/disable that raced a refresh without rotation.
            await Account(id, revision, true, ct);
            cache[id] = new(revision, p.AuthEpoch, token.AccessToken, time.GetUtcNow().AddSeconds(token.ExpiresSeconds));
            observation.Complete();
            return token.AccessToken;
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { observation.Fail(ex); throw; }
        catch (Exception ex)
        {
            var failure = EmailOperationObservation.Failure(ex, logger, diagnosticIds, "oauth.refresh", id);
            observation.Fail(failure);
            throw failure;
        }
        finally { gate.Release(); }
    }
    internal async ValueTask RejectAuthorizationAsync(Guid id, long revision, CancellationToken ct)
    {
        var c = await Account(id, revision, true, ct); var p = c.EmailAccount!;
        await credentials.SaveAsync(c with { ProtectedPayload = "", SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1,
            EmailAccount = p with { ConnectionStatus = EmailConnectionStatus.ReconnectRequired, AuthEpoch = p.AuthEpoch + 1 }, UpdatedAtUtc = time.GetUtcNow() }, revision, ct, c.SecretRevision);
        cache.TryRemove(id, out _);
    }
    public async ValueTask<EmailOAuthConnection> CheckAsync(Guid id, long revision, CancellationToken ct)
    {
        using var observation = new EmailOperationObservation("oauth.check", synthetic, time, id, ct);
        var c = await Account(id, revision, true, ct);
        if (observations.TryGetValue(id, out var last) && last.LastCheckedAtUtc > time.GetUtcNow().AddSeconds(-5)) { observation.Complete("throttled"); return await ConnectionAsync(id, ct); }
        try
        {
            var token = await AccessTokenAsync(id, revision, ct, forceRefresh: true);
            if (!synthetic) await VerifyMailbox(c.EmailAccount!.EmailAddress, token, id, "oauth.check", ct);
            observations[id] = Status(c) with { LastCheckedAtUtc = time.GetUtcNow() };
            observation.Complete();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested) { observation.Fail(ex); throw; }
        catch (Exception ex)
        {
            if (ex is AgentCoreException { Code: "email_oauth_identity_mismatch" or "email_oauth_reconnect_required" or "email_oauth_scope_missing" })
            { try { await RejectAuthorizationAsync(id, revision, ct); } catch (AgentCoreException) { /* A concurrent authority change already fences this check. */ } }
            var failure = EmailOperationObservation.Failure(ex, logger, diagnosticIds, "oauth.check", id);
            observation.Fail(failure);
            observations[id] = Status(c, failure.Code + ": " + failure.Message) with { LastCheckedAtUtc = time.GetUtcNow(), DiagnosticId = failure.DiagnosticId };
        }
        return await ConnectionAsync(id, ct);
    }
    public async ValueTask<EmailOAuthConnection> DisconnectAsync(Guid id, long revision, CancellationToken ct)
    {
        using var observation = new EmailOperationObservation("oauth.disconnect", synthetic, time, id, ct);
        var c = await Account(id, revision, false, ct); var p = c.EmailAccount!;
        await credentials.SaveAsync(c with { ProtectedPayload = "", SecretRevision = c.SecretRevision + 1, Revision = c.Revision + 1,
            EmailAccount = p with { ConnectionStatus = EmailConnectionStatus.Disconnected, AuthEpoch = p.AuthEpoch + 1 }, UpdatedAtUtc = time.GetUtcNow() }, revision, ct, c.SecretRevision);
        cache.TryRemove(id, out _);
        lock (pendingGate) foreach (var key in pending.Where(a => a.Value.AccountId == id).Select(a => a.Key).ToArray()) pending.Remove(key);
        var revoked = synthetic || c.ProtectedPayload == "";
        Guid? diagnostic = null;
        if (!revoked)
        {
            try
            {
                using var client = clients.CreateClient(HttpClientName);
                using var response = await client.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = GoogleOAuthRefreshGrant.Read(protector.Unprotect(id, c.ProtectionVersion, c.ProtectedPayload)).Token }), ct);
                revoked = response.IsSuccessStatusCode;
                if (!revoked) diagnostic = HandledFailureLog.Warning(logger, new HttpRequestException("Google revocation was not confirmed.", null, response.StatusCode),
                    "email.oauth.disconnect", "revocation_unconfirmed", new DiagnosticContext(ProviderAlias: "gmail", CredentialId: id), diagnostics: diagnosticIds);
            }
            catch (Exception ex)
            {
                // Local disconnect remains committed even if provider revocation fails or is cancelled.
                diagnostic = HandledFailureLog.Warning(logger, ex, "email.oauth.disconnect", "revocation_unconfirmed",
                    new DiagnosticContext(ProviderAlias: "gmail", CredentialId: id), diagnostics: diagnosticIds);
            }
        }
        observations[id] = new("Disconnected", synthetic || options.Configured, synthetic, revoked ? null : "Disconnected locally. Google revocation could not be confirmed. Remove access in your Google account settings.", ProviderRevoked: revoked, DiagnosticId: diagnostic);
        observation.Complete(revoked ? "success" : "revocation_unconfirmed");
        return await ConnectionAsync(id, CancellationToken.None);
    }
    private async Task<Token> Exchange(Dictionary<string, string> fields, Guid id, string operation, CancellationToken ct, GoogleOAuthClient? selectedClient = null)
    {
        var clientSettings = selectedClient ?? options.Client ?? throw new InvalidOperationException("Google OAuth client is unavailable.");
        fields["client_id"] = clientSettings.ClientId; fields["client_secret"] = clientSettings.ClientSecret;
        using var client = clients.CreateClient(HttpClientName);
        using var response = await SendBoundedAsync(client,
            () => new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token") { Content = new FormUrlEncodedContent(fields) },
            fields["grant_type"] == "refresh_token", time, ct);
        using var json = await ReadJson(response, 64 * 1024, ct);
        var root = json.RootElement;
        if (!response.IsSuccessStatusCode)
        {
            var failure = root.TryGetProperty("error", out var e) && e.GetString() == "invalid_grant"
                ? Failure("email_oauth_reconnect_required", "Google authorization is no longer valid. Reconnect.", 403)
                : Failure("email_oauth_temporary_failure", "Google authorization is temporarily unavailable. Try again.", 502);
            throw EmailOperationObservation.Failure(new HttpRequestException("Google token request was rejected.", null, response.StatusCode),
                logger, diagnosticIds, operation, id, normalized: failure);
        }
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (string.IsNullOrWhiteSpace(access)) throw Failure("email_oauth_temporary_failure", "Google returned incomplete authorization. Try again.", 502);
        return new(access, root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            root.TryGetProperty("id_token", out var i) ? i.GetString() : null,
            root.TryGetProperty("scope", out var s) ? (s.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries) : [],
            root.TryGetProperty("expires_in", out var expiry) && expiry.TryGetInt32(out var seconds) ? Math.Clamp(seconds, 60, 86400) : 3600);
    }
    private async Task VerifyMailbox(string expected, string access, Guid id, string operation, CancellationToken ct)
    {
        using var client = clients.CreateClient(HttpClientName); using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        using var json = await ReadJson(response, 64 * 1024, ct);
        if (!response.IsSuccessStatusCode)
        {
            var quota = json.RootElement.TryGetProperty("error", out var problem) && problem.ValueKind == JsonValueKind.Object
                && problem.TryGetProperty("errors", out var causes) && causes.ValueKind == JsonValueKind.Array
                && causes.EnumerateArray().Any(item => item.TryGetProperty("reason", out var reason) && reason.GetString() is "rateLimitExceeded" or "userRateLimitExceeded" or "dailyLimitExceeded" or "quotaExceeded");
            var failure = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? Failure("email_oauth_reconnect_required", "Google rejected authorization. Reconnect the original account.", 403)
                : response.StatusCode == System.Net.HttpStatusCode.Forbidden && !quota
                    ? Failure("email_oauth_scope_missing", "Google denied mailbox permissions. Reconnect or review operator setup.", 403)
                    : Failure("email_oauth_temporary_failure", "Google mailbox verification is temporarily unavailable. Retry check.", 502);
            throw EmailOperationObservation.Failure(new HttpRequestException("Google mailbox verification was rejected.", null, response.StatusCode),
                logger, diagnosticIds, operation, id, normalized: failure);
        }
        if (!json.RootElement.TryGetProperty("emailAddress", out var email) || !string.Equals(expected, email.GetString(), StringComparison.OrdinalIgnoreCase))
            throw Failure("email_oauth_identity_mismatch", "Gmail mailbox verification failed. Reconnect the original account.", 403);
    }
    internal static async Task<HttpResponseMessage> SendBoundedAsync(HttpClient client, Func<HttpRequestMessage> request, bool retrySafe, TimeProvider time, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var outgoing = request();
            try
            {
                var response = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!retrySafe || attempt >= 1 || (int)response.StatusCode != 429 && (int)response.StatusCode < 500) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (retrySafe && attempt < 1) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), time, ct);
        }
    }
    internal static async Task<JsonDocument> ReadJson(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw Failure("email_provider_response_invalid", "Google response exceeded the safe limit.", 502);
        await using var input = await response.Content.ReadAsStreamAsync(ct); using var output = new MemoryStream(); var buffer = new byte[8192];
        while (true) { var count = await input.ReadAsync(buffer, ct); if (count == 0) break; if (output.Length + count > limit) throw Failure("email_provider_response_invalid", "Google response exceeded the safe limit.", 502); output.Write(buffer, 0, count); }
        return JsonDocument.Parse(output.ToArray());
    }
    private static string Form(Dictionary<string, string> values) => string.Join('&', values.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
}
