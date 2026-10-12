using System.Net;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Time.Testing;
using AgentCore.Application.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class GoogleEmailOAuthTests
{
    [Fact]
    public async Task Callback_verifies_PKCE_nonce_identity_scopes_and_encrypts_offline_grant_then_rotates_without_revision()
    {
        using var f = new Fixture();
        var account = await f.Create(); var start = await f.Service.StartAsync(account.CredentialId, 1, f.Owner, default);
        var query = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query);
        Assert.Equal("S256", query["code_challenge_method"]); Assert.Equal("offline", query["access_type"]); Assert.Equal("consent", query["prompt"]);
        f.Verifier.ExpectedNonce = query["nonce"]!;
        await f.Complete(start);
        var c = (await f.Store.GetAsync(account.CredentialId))!;
        Assert.Equal("verified@example.test", c.EmailAccount!.EmailAddress); Assert.Equal("verified-sub", c.EmailAccount.GoogleSubject);
        Assert.DoesNotContain("known-refresh", c.ProtectedPayload);
        Assert.Equal("known-refresh", f.Protector.Unprotect(c.CredentialId, 1, c.ProtectedPayload));
        Assert.Contains("code_verifier=", f.Transport.ExchangeBody);
        var sentVerifier = QueryHelpers.ParseQuery("?" + f.Transport.ExchangeBody)["code_verifier"].ToString();
        Assert.Equal(query["code_challenge"].ToString(), GmailMime.EncodeBase64Url(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(sentVerifier)))); Assert.DoesNotContain("known-refresh", JsonSerializer.Serialize(await f.Credentials.GetAsync(c.CredentialId)));
        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.Service.AccessTokenAsync(c.CredentialId, c.Revision, default).AsTask()));
        Assert.All(tokens, t => Assert.Equal("known-access", t)); Assert.Equal(1, f.Transport.Refreshes);
        var rotated = (await f.Store.GetAsync(c.CredentialId))!;
        Assert.Equal(c.Revision, rotated.Revision); Assert.Equal(c.SecretRevision + 1, rotated.SecretRevision);
        Assert.Equal("rotated-refresh", f.Protector.Unprotect(c.CredentialId, 1, rotated.ProtectedPayload));
        var reopened = f.NewService(); await reopened.AccessTokenAsync(c.CredentialId, c.Revision, default);
        Assert.Equal(2, f.Transport.Refreshes);
    }
    [Theory]
    [InlineData("browser")]
    [InlineData("owner")]
    [InlineData("expiry")]
    [InlineData("disable")]
    [InlineData("state")]
    public async Task Invalid_correlation_rejected_before_exchange(string reason)
    {
        using var f = new Fixture(); var c = await f.Create(); var start = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default);
        if (reason == "expiry") f.Time.Advance(TimeSpan.FromMinutes(6));
        if (reason == "disable") await f.Credentials.UpdateAsync(c.CredentialId, 1, "Gmail", "Disabled", null, []);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.CompleteAsync(reason == "state" ? "forged" : f.State(start), reason == "browser" ? "forged" : start.Correlation,
            reason == "owner" ? Guid.NewGuid() : f.Owner, "code", null, default).AsTask());
        Assert.Equal(0, f.Transport.Exchanges); Assert.Equal("", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
    }
    [Theory]
    [InlineData("refresh")]
    [InlineData("scope")]
    [InlineData("identity")]
    [InlineData("nonce")]
    public async Task Failed_exchange_cannot_commit_or_replay(string reason)
    {
        using var f = new Fixture(); var c = await f.Create("original@example.test"); var start = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default);
        f.Transport.MissingRefresh = reason == "refresh"; f.Transport.MissingScope = reason == "scope";
        f.Verifier.Email = reason == "identity" ? "different@example.test" : "original@example.test";
        f.Verifier.Reject = reason == "nonce";
        await f.Complete(start);
        Assert.NotNull((await f.Service.ConnectionAsync(c.CredentialId, default)).Error);
        Assert.Equal("", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Complete(start)); Assert.Equal(1, f.Transport.Exchanges);
    }
    [Fact]
    public async Task Denial_disconnect_reconnect_same_subject_and_stale_callback_fences()
    {
        using var f = new Fixture(); var c = await f.Create(); var denied = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default);
        await f.Service.CompleteAsync(f.State(denied), denied.Correlation, f.Owner, null, "access_denied", default);
        Assert.Contains("denied", (await f.Service.ConnectionAsync(c.CredentialId, default)).Error!);
        var start = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default); await f.Complete(start);
        var pending = await f.Service.StartAsync(c.CredentialId, 2, f.Owner, default);
        f.Transport.RevokeFails = true;
        var disconnected = await f.Service.DisconnectAsync(c.CredentialId, 2, default);
        Assert.False(disconnected.ProviderRevoked); Assert.Contains("locally", disconnected.Error!);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Complete(pending));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.AccessTokenAsync(c.CredentialId, 2, default).AsTask());
        start = await f.Service.StartAsync(c.CredentialId, 3, f.Owner, default); f.Verifier.Subject = "other-sub";
        await f.Complete(start); Assert.Contains("identity_mismatch", (await f.Service.ConnectionAsync(c.CredentialId, default)).Error!);
        Assert.Equal("verified-sub", (await f.Store.GetAsync(c.CredentialId))!.EmailAccount!.GoogleSubject);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Permanent_grant_failure_is_distinct_from_temporary_failure(bool permanent)
    {
        using var f = new Fixture(); var c = await f.Create(); await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default));
        f.Transport.RefreshFailure = permanent ? "invalid_grant" : "server_error";
        var status = await f.Service.CheckAsync(c.CredentialId, 2, default);
        Assert.Equal(permanent ? "ReconnectRequired" : "Connected", status.Status);
        Assert.Contains(permanent ? "reconnect_required" : "temporary_failure", status.Error!);
    }
    [Fact]
    public async Task Explicit_config_and_Synthetic_never_fall_back_to_global_passwords()
    {
        using var f = new Fixture(); var c = await f.Create();
        var missing = new GoogleEmailOAuthService(f.Store, f.Protector, f.Transport, new(), f.Time, false);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => missing.StartAsync(c.CredentialId, 1, f.Owner, default).AsTask()); Assert.Equal("email_oauth_not_configured", error.Code);
        var syn = new GoogleEmailOAuthService(f.Store, f.Protector, f.Transport, new(), f.Time, true);
        var start = await syn.StartAsync(c.CredentialId, 1, f.Owner, default);
        Assert.True(start.Synthetic); Assert.StartsWith("/api/", start.AuthorizationUrl);
        var state = QueryHelpers.ParseQuery(start.AuthorizationUrl[(start.AuthorizationUrl.IndexOf('?'))..])["state"].ToString();
        await syn.CompleteAsync(state, start.Correlation, f.Owner, "consent", null, default);
        Assert.True((await f.Store.GetAsync(c.CredentialId))!.EmailAccount!.Synthetic); Assert.Equal(0, f.Transport.Exchanges);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.AccessTokenAsync(c.CredentialId, 2, default).AsTask());
    }
    [Fact]
    public async Task Disconnect_during_refresh_blocks_late_rotation_and_cached_use()
    {
        using var f = new Fixture(); var c = await f.Create(); await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default));
        f.Transport.PauseRefresh = true;
        var refreshing = f.Service.AccessTokenAsync(c.CredentialId, 2, default).AsTask();
        await f.Transport.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await f.Service.DisconnectAsync(c.CredentialId, 2, default);
        f.Transport.ReleaseRefresh.TrySetResult();
        await Assert.ThrowsAsync<AgentCoreException>(() => refreshing);
        var saved = (await f.Store.GetAsync(c.CredentialId))!; Assert.Equal("", saved.ProtectedPayload); Assert.Equal(EmailConnectionStatus.Disconnected, saved.EmailAccount!.ConnectionStatus);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Service.AccessTokenAsync(c.CredentialId, saved.Revision, default).AsTask());
    }
    [Fact]
    public async Task Parallel_callbacks_commit_once_and_duplicate_subject_is_rejected()
    {
        using var f = new Fixture(); var c = await f.Create(); var start = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default);
        var callbacks = new[] { f.Complete(start), f.Complete(start) };
        foreach (var callback in callbacks) { try { await callback; } catch (AgentCoreException) { } }
        Assert.Equal(1, callbacks.Count(t => t.IsCompletedSuccessfully)); Assert.Equal(1, f.Transport.Exchanges);
        var duplicate = await f.Create(); await f.Complete(await f.Service.StartAsync(duplicate.CredentialId, 1, f.Owner, default));
        Assert.Contains("Conflict", (await f.Service.ConnectionAsync(duplicate.CredentialId, default)).Error!); Assert.Equal("", (await f.Store.GetAsync(duplicate.CredentialId))!.ProtectedPayload);
    }
    [Fact]
    public async Task Production_ID_verifier_rejects_malformed_JWT_without_identity_claims()
    { await Assert.ThrowsAnyAsync<Exception>(() => new GoogleEmailIdentityVerifier().VerifyAsync("forged-token", "client", "nonce", default)); }
    [Theory]
    [InlineData("http://public.example.test/")]
    [InlineData("https://trusted.example.test/other")]
    [InlineData("https://user@trusted.example.test/")]
    [InlineData("https://trusted.example.test/?returnUrl=https://evil.example")]
    [InlineData("https://trusted.example.test/#fragment")]
    public void OAuth_public_origin_rejects_unsafe_or_ambiguous_configuration(string value)
    { Assert.False(new GoogleEmailOAuthOptions { ClientId = "client", ClientSecret = "secret", PublicAppUrl = value }.Configured); }
    [Fact]
    public async Task Callback_expiring_during_identity_verification_never_commits_grant()
    {
        using var f = new Fixture(); var c = await f.Create(); var start = await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default);
        f.Verifier.BeforeReturn = () => f.Time.Advance(TimeSpan.FromMinutes(6)); await f.Complete(start);
        Assert.Contains("state_invalid", (await f.Service.ConnectionAsync(c.CredentialId, default)).Error!); Assert.Equal("", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
    }
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Connected")]
    [InlineData(HttpStatusCode.Unauthorized, "ReconnectRequired")]
    public async Task Mailbox_check_distinguishes_temporary_provider_failure_from_revoked_authorization(HttpStatusCode status, string expected)
    {
        using var f = new Fixture(); var c = await f.Create(); await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default));
        f.Transport.ProfileFailure = status; var result = await f.Service.CheckAsync(c.CredentialId, 2, default);
        Assert.Equal(expected, result.Status); Assert.NotNull(result.Error);
        Assert.Equal(expected == "Connected", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload.Length > 0);
    }
    [Theory]
    [InlineData("invalid_grant", "ReconnectRequired")]
    [InlineData("server_error", "Connected")]
    public async Task Refresh_failure_reuses_one_safe_reference_in_owner_check_and_monitoring(string reason, string expectedStatus)
    {
        var id = Guid.NewGuid(); var unused = Guid.NewGuid(); var ids = new QueueDiagnosticIdSource([id, unused]);
        var logs = new DiagnosticLogCapture<GoogleEmailOAuthService>(); using var metrics = RuntimeMetricProbe.Start();
        using var f = new Fixture(logs, ids); var c = await f.Create(); await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default));
        f.Transport.RefreshFailure = reason;
        var result = await f.Service.CheckAsync(c.CredentialId, 2, default);
        Assert.Equal(expectedStatus, result.Status); Assert.Equal(id, result.DiagnosticId); Assert.Equal(unused, ids.NewId());
        var entry = Assert.Single(logs.Entries); Assert.Equal("email.oauth.refresh", entry.Properties["Operation"]);
        Assert.Equal(id, entry.Properties["DiagnosticId"]); Assert.Equal(c.CredentialId, entry.Properties["CredentialId"]);
        Assert.Contains("StatusCode=400", entry.Exception!.ToString());
        var rendered = entry.Message + entry.Exception;
        Assert.DoesNotContain("known-client-secret", rendered); Assert.DoesNotContain("known-refresh", rendered); Assert.DoesNotContain("verified@example.test", rendered);
        Assert.Contains(metrics.Tags, t => t.Instrument == "email.operations" && t.Key == "operation" && Equals(t.Value, "oauth.check"));
        Assert.All(metrics.Tags.Where(t => t.Instrument.StartsWith("email.", StringComparison.Ordinal)), t => Assert.Contains(t.Key, new[] { "provider", "operation", "outcome", "profile" }));
    }

    [Fact]
    public async Task Unconfirmed_remote_revocation_logs_reference_after_local_disconnect_commits()
    {
        var id = Guid.NewGuid(); var logs = new DiagnosticLogCapture<GoogleEmailOAuthService>();
        using var f = new Fixture(logs, new QueueDiagnosticIdSource([id])); var c = await f.Create();
        await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default)); f.Transport.RevokeFails = true;
        var result = await f.Service.DisconnectAsync(c.CredentialId, 2, default);
        Assert.Equal("Disconnected", result.Status); Assert.False(result.ProviderRevoked); Assert.Equal(id, result.DiagnosticId);
        Assert.Equal("", (await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
        var entry = Assert.Single(logs.Entries); Assert.Equal("revocation_unconfirmed", entry.Properties["ErrorCode"]);
        Assert.Contains("StatusCode=503", entry.Exception!.ToString()); Assert.DoesNotContain("known-refresh", entry.Exception.ToString());
    }

    [Fact]
    public async Task Disabled_OAuth_logger_preserves_safe_failure_without_allocating_an_id()
    {
        var id = Guid.NewGuid(); var ids = new QueueDiagnosticIdSource([id]);
        using var f = new Fixture(NullLogger<GoogleEmailOAuthService>.Instance, ids); var c = await f.Create();
        await f.Complete(await f.Service.StartAsync(c.CredentialId, 1, f.Owner, default)); f.Transport.RefreshFailure = "invalid_grant";
        var result = await f.Service.CheckAsync(c.CredentialId, 2, default);
        Assert.Null(result.DiagnosticId); Assert.Equal(id, ids.NewId()); Assert.Equal("ReconnectRequired", result.Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "google-oauth-" + Guid.NewGuid().ToString("N"));
        public readonly Guid Owner = Guid.NewGuid(); public readonly FakeTimeProvider Time = new(DateTimeOffset.UtcNow);
        public readonly ICredentialStore Store; public readonly ICredentialProtector Protector; public readonly CredentialService Credentials;
        public readonly Transport Transport = new(); public readonly Verifier Verifier = new(); public readonly GoogleEmailOAuthService Service;
        private readonly ILogger<GoogleEmailOAuthService>? logger; private readonly IDiagnosticIdSource? diagnosticIds;
        public Fixture(ILogger<GoogleEmailOAuthService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
        { this.logger = logger; this.diagnosticIds = diagnosticIds; var instances = new InMemoryAgentInstanceStore(); var store = new InMemoryCredentialStore(instances); Store = store; Protector = new LocalCredentialProtector(root); Credentials = new(store, store, Protector, instances, new SystemIdGenerator(Time), Time); Service = NewService(); }
        public GoogleEmailOAuthService NewService() => new(Store, Protector, Transport, new() { ClientId = "client", ClientSecret = "known-client-secret", PublicAppUrl = "https://app.example.test" }, Time, false, Verifier, logger, diagnosticIds);
        public async Task<CredentialView> Create(string email = "") => await Credentials.CreateEmailAccountAsync("Gmail", EmailAccountProfile.Gmail(email), "");
        public string State(EmailOAuthStart start) => QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query)["state"].ToString();
        public async Task Complete(EmailOAuthStart start) => await Service.CompleteAsync(State(start), start.Correlation, Owner, "code", null, default);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Verifier : IGoogleEmailIdentityVerifier
    {
        public string Email = "verified@example.test", Subject = "verified-sub"; public string? ExpectedNonce; public bool Reject; public Action? BeforeReturn;
        public Task<GoogleEmailIdentity> VerifyAsync(string token, string client, string nonce, CancellationToken ct)
        { Assert.Equal("client", client); if (ExpectedNonce is not null) Assert.Equal(ExpectedNonce, nonce); if (Reject) throw new InvalidOperationException("invalid nonce"); BeforeReturn?.Invoke(); return Task.FromResult(new GoogleEmailIdentity(Subject, Email)); }
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public readonly TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously), ReleaseRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool PauseRefresh;
        public HttpStatusCode? ProfileFailure;
        public int Exchanges, Refreshes; public bool MissingRefresh, MissingScope, RevokeFails; public string? RefreshFailure; public string ExchangeBody = "";
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/revoke") return new(RevokeFails ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
            if (request.RequestUri.AbsolutePath.EndsWith("/profile")) return ProfileFailure is { } failed ? Json(new { error = "safe-error" }, failed) : Json(new { emailAddress = "verified@example.test" });
            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("grant_type=refresh_token")) { Interlocked.Increment(ref Refreshes); if (PauseRefresh) { RefreshStarted.TrySetResult(); await ReleaseRefresh.Task.WaitAsync(ct); } if (RefreshFailure is not null) return Json(new { error = RefreshFailure }, HttpStatusCode.BadRequest); }
            else { Exchanges++; ExchangeBody = body; }
            return Json(new { access_token = "known-access", refresh_token = MissingRefresh ? null : body.Contains("grant_type=refresh_token") ? "rotated-refresh" : "known-refresh", id_token = "verified-id", expires_in = 3600,
                scope = EmailAccountProfile.ReadScope + (MissingScope ? "" : " " + EmailAccountProfile.SendScope) });
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
