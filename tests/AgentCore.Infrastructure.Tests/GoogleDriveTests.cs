using System.Net;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Drive;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.WebUtilities;
namespace AgentCore.Infrastructure.Tests;

public sealed partial class GoogleDriveTests
{
    [Fact]
    public async Task Pkce_identity_scope_rotation_restart_disconnect_and_reconnect_are_separate_from_Email()
    {
        using var f = new Fixture(); var c = await f.Credentials.CreateGoogleDriveAccountAsync("Work Drive"); var start = await f.Start(c); var q = QueryHelpers.ParseQuery(new Uri(start.AuthorizationUrl).Query);
        Assert.Equal("S256", q["code_challenge_method"]); Assert.Equal("false", q["include_granted_scopes"]); Assert.Equal("openid email " + DriveAccountProfile.Scope, q["scope"]); Assert.EndsWith(GoogleDriveOAuthOptions.CallbackPath, q["redirect_uri"].ToString());
        await f.Complete(start); var connected = (await f.Store.GetAsync(c.CredentialId))!; Assert.Equal("work@example.test", connected.DriveAccount!.EmailAddress); Assert.Null(connected.EmailAccount); Assert.DoesNotContain("secret-refresh", connected.ProtectedPayload);
        var verifier = QueryHelpers.ParseQuery("?" + f.Transport.ExchangeBody)["code_verifier"].ToString(); Assert.Equal(q["code_challenge"].ToString(), GoogleOAuthProof.Challenge(verifier));
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Complete(start));
        var tokens = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => f.OAuth.AccessTokenAsync(c.CredentialId, 2, default).AsTask())); Assert.All(tokens, t => Assert.Equal("access", t)); Assert.Equal(1, f.Transport.Refreshes);
        var rotated = (await f.Store.GetAsync(c.CredentialId))!; Assert.Equal(connected.Revision, rotated.Revision); Assert.Equal(connected.SecretRevision + 1, rotated.SecretRevision);
        await f.NewService().AccessTokenAsync(c.CredentialId, 2, default); Assert.Equal(2, f.Transport.Refreshes);
        await f.OAuth.DisconnectAsync(c.CredentialId, 2, default); Assert.Equal("Disconnected", (await f.OAuth.ConnectionAsync(c.CredentialId, default)).Status); Assert.Empty((await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload);
        await Assert.ThrowsAsync<AgentCoreException>(() => f.OAuth.AccessTokenAsync(c.CredentialId, 2, default).AsTask());
        var reconnect = await f.Start(await f.Credentials.GetAsync(c.CredentialId)); await f.Complete(reconnect); Assert.Equal(3, (await f.Store.GetAsync(c.CredentialId))!.DriveAccount!.AuthEpoch);
    }
    [Theory]
    [InlineData("scope")]
    [InlineData("broad")]
    [InlineData("refresh")]
    [InlineData("identity")]
    [InlineData("cookie")]
    [InlineData("owner")]
    public async Task Invalid_grants_or_correlation_never_commit(string reason)
    {
        using var f = new Fixture(); var c = await f.Credentials.CreateGoogleDriveAccountAsync("Drive"); var start = await f.Start(c);
        if (reason == "scope") f.Transport.Scope = "openid email"; if (reason == "broad") f.Transport.Scope = "https://www.googleapis.com/auth/drive"; if (reason == "refresh") f.Transport.MissingRefresh = true; if (reason == "identity") f.Transport.IdentityEmail = "other@example.test";
        await Assert.ThrowsAsync<AgentCoreException>(() => f.OAuth.CompleteAsync(f.State(start), reason == "cookie" ? "forged" : start.Correlation, reason == "owner" ? Guid.NewGuid() : f.Owner, "code", null, default).AsTask());
        Assert.Empty((await f.Store.GetAsync(c.CredentialId))!.ProtectedPayload); Assert.Equal(0, (await f.Store.GetAsync(c.CredentialId))!.DriveAccount!.AuthEpoch);
    }
    [Theory]
    [InlineData(403, "drive_scope_limited", 1)]
    [InlineData(404, "drive_not_found", 1)]
    [InlineData(409, "drive_remote_conflict", 1)]
    [InlineData(429, "drive_rate_limited", 3)]
    [InlineData(503, "drive_provider_unavailable", 3)]
    [InlineData(401, "drive_access_revoked", 2)]
    public async Task Transport_statuses_have_bounded_read_retries_and_safe_recovery(int status, string code, int attempts)
    {
        using var f = new Fixture(); var c = await f.Credentials.CreateGoogleDriveAccountAsync("Drive"); await f.Complete(await f.Start(c)); f.Transport.FileStatus = (HttpStatusCode)status; var p = f.Provider(c.CredentialId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => p.GetAsync("file-1", default).AsTask()); Assert.Equal(code, error.Code); Assert.Equal(attempts, f.Transport.FileRequests); Assert.DoesNotContain("provider-secret", error.Message);
        if (status == 401) Assert.Equal(DriveConnectionStatus.ReconnectRequired, (await f.Store.GetAsync(c.CredentialId))!.DriveAccount!.ConnectionStatus);
    }
    [Fact]
    public async Task Writes_are_not_retried_and_resumable_location_must_be_official()
    {
        using var f = new Fixture(); var c = await f.Credentials.CreateGoogleDriveAccountAsync("Drive"); await f.Complete(await f.Start(c)); var p = f.Provider(c.CredentialId);
        f.Transport.FileStatus = HttpStatusCode.ServiceUnavailable; await Assert.ThrowsAsync<AgentCoreException>(() => p.CreateFolderAsync("file-1", "Reports", "root", default).AsTask()); Assert.Equal(1, f.Transport.FileRequests);
        f.Transport.FileStatus = HttpStatusCode.OK; f.Transport.UploadLocation = "https://evil.example.test/upload/drive/session";
        var op = new DriveOperation(Guid.NewGuid(), f.Owner, c.CredentialId, "hash", 1, "drive.upload", "file-1", "{}", "Dispatched", 1, DateTimeOffset.UtcNow); await f.Operations.ClaimAsync(op, default);
        var ex = await Assert.ThrowsAsync<AgentCoreException>(() => p.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask()); Assert.Equal("drive_effect_indeterminate", ex.Code); Assert.Equal(0, f.Transport.UploadPuts);
        f.Transport.UploadLocation = "https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque"; await p.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default);
        Assert.Equal(1, f.Transport.UploadPuts); Assert.DoesNotContain("opaque", (await f.Operations.GetAsync(f.Owner, op.OperationId, default))!.ProtectedUploadSession);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.GetAsync("file-1", canceled.Token).AsTask());
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "drive-tests-" + Guid.NewGuid().ToString("N")); public readonly Guid Owner = Guid.NewGuid(); public readonly InMemoryCredentialStore Store; public readonly LocalCredentialProtector Protector; public readonly CredentialService Credentials; public readonly Transport Transport = new(); public readonly InMemoryDriveStore Operations = new(); public readonly GoogleDriveOAuthService OAuth;
        public Fixture() { var instances = new InMemoryAgentInstanceStore(); Store = new(instances); Protector = new(root); Credentials = new(Store, Store, Protector, instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System); OAuth = NewService(); }
        public GoogleDriveOAuthService NewService() => new(Store, Protector, Transport, new() { Enabled = true }, new() { ClientId = "client", ClientSecret = "secret-client", PublicAppUrl = "https://app.example.test" }, TimeProvider.System, false) { IdentityProof = (token, client, nonce, ct) => Task.FromResult(new GoogleOAuthIdentity("subject", "work@example.test")) };
        public Task<DriveOAuthStart> Start(CredentialView c) => OAuth.StartAsync(c.CredentialId, c.Revision, Owner, default).AsTask(); public string State(DriveOAuthStart s) => QueryHelpers.ParseQuery(new Uri(s.AuthorizationUrl).Query)["state"].ToString(); public Task Complete(DriveOAuthStart s) => OAuth.CompleteAsync(State(s), s.Correlation, Owner, "code", null, default).AsTask();
        public GoogleDriveProvider Provider(Guid id) => new(id, (ct, fresh) => OAuth.AccessTokenAsync(id, 2, ct, fresh), ct => OAuth.RejectAuthorizationAsync(id, 2, ct), Transport, Operations, Protector, TimeProvider.System); public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpMethod? LastFileMethod; public string LastFilePath = "", LastFileBody = "";
        public string Scope = DriveAccountProfile.Scope, IdentityEmail = "work@example.test", ExchangeBody = "", UploadLocation = "https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque"; public bool MissingRefresh; public int Refreshes, FileRequests, UploadPuts; public HttpStatusCode FileStatus = HttpStatusCode.OK;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? UploadHandler;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? DownloadHandler;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var path = request.RequestUri!.AbsolutePath;
            if (path == "/token") { var body = await request.Content!.ReadAsStringAsync(ct); if (body.Contains("grant_type=refresh_token")) Refreshes++; else ExchangeBody = body; return Json(new { access_token = "access", refresh_token = MissingRefresh ? null : "secret-refresh", id_token = "id", expires_in = 3600, scope = Scope }); }
            if (path == "/revoke") return Json(new { });
            if (path.EndsWith("/about")) return Json(new { user = new { emailAddress = IdentityEmail } });
            if (path.StartsWith("/upload/")) { if (UploadHandler is not null) return await UploadHandler(request, ct); if (request.Method == HttpMethod.Put) { UploadPuts++; return Json(new { id = "file-1" }); } var r = Json(new { }); r.Headers.Location = new Uri(UploadLocation); return r; }
            LastFileMethod = request.Method; LastFilePath = path; LastFileBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            FileRequests++; if (FileStatus != HttpStatusCode.OK) return Json(new { error = "provider-secret" }, FileStatus);
            if (request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal) && DownloadHandler is not null) return await DownloadHandler(request, ct);
            return Json(new { id = "file-1", name = "report.txt", mimeType = "text/plain", size = "2", version = "1", md5Checksum = "49f68a5c8493ec2c0bf489821c21fc3b", parents = new[] { "root" }, trashed = false });
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
