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
    [Theory]
    [InlineData(403, "drive_scope_limited", 1)]
    [InlineData(404, "drive_not_found", 1)]
    [InlineData(409, "drive_remote_conflict", 1)]
    [InlineData(429, "drive_rate_limited", 3)]
    [InlineData(503, "drive_provider_unavailable", 3)]
    [InlineData(401, "drive_access_revoked", 2)]
    public async Task Transport_statuses_have_bounded_read_retries_and_safe_recovery(int status, string code, int attempts)
    {
        using var f = new Fixture(); var c = new { CredentialId = Guid.NewGuid() }; f.Transport.FileStatus = (HttpStatusCode)status; var p = f.Provider(c.CredentialId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => p.GetAsync("file-1", default).AsTask()); Assert.Equal(code, error.Code); Assert.Equal(attempts, f.Transport.FileRequests); Assert.DoesNotContain("provider-secret", error.Message);
        if (status == 401) Assert.True(f.ReconnectRequired);
    }
    [Fact]
    public async Task Writes_are_not_retried_and_resumable_location_must_be_official()
    {
        using var f = new Fixture(); var c = new { CredentialId = Guid.NewGuid() }; var p = f.Provider(c.CredentialId);
        f.Transport.FileStatus = HttpStatusCode.ServiceUnavailable; await Assert.ThrowsAsync<AgentCoreException>(() => p.CreateFolderAsync("file-1", "Reports", "root", default).AsTask()); Assert.Equal(1, f.Transport.FileRequests);
        f.Transport.FileStatus = HttpStatusCode.OK; f.Transport.UploadLocation = "https://evil.example.test/upload/drive/session";
        var op = new DriveOperation(Guid.NewGuid(), f.Owner, c.CredentialId, "hash", 1, "drive.upload", "file-1", "{}", "Dispatched", 1, DateTimeOffset.UtcNow); await f.Operations.ClaimAsync(op, default);
        var ex = await Assert.ThrowsAsync<AgentCoreException>(() => p.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask()); Assert.Equal("drive_effect_indeterminate", ex.Code); Assert.Equal(0, f.Transport.UploadPuts);
        f.Transport.UploadLocation = "https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque"; await p.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default);
        Assert.Equal(1, f.Transport.UploadPuts); Assert.DoesNotContain("opaque", (await f.Operations.GetAsync(f.Owner, op.OperationId, default))!.ProtectedUploadSession);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.GetAsync("file-1", canceled.Token).AsTask());
    }
    // A native-protocol fixture, not an account/OAuth authority owner. The
    // canonical tenant suites separately qualify proof, token and IAM handling.
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "drive-protocol-" + Guid.NewGuid().ToString("N"));
        public readonly Guid Owner = Guid.NewGuid();
        public readonly LocalCredentialProtector Protector;
        public readonly Transport Transport = new();
        public readonly InMemoryDriveStore Operations = new();
        public bool ReconnectRequired;
        public Fixture() { Protector = new(root); }
        public GoogleDriveProvider Provider(Guid id) => new(id, (_, _) => ValueTask.FromResult("access"),
            _ => { ReconnectRequired = true; return ValueTask.CompletedTask; }, Transport, Operations, Protector, TimeProvider.System);
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Transport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpMethod? LastFileMethod; public string LastFilePath = "", LastFileBody = "";
        public string UploadLocation = "https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque"; public int FileRequests, UploadPuts; public HttpStatusCode FileStatus = HttpStatusCode.OK;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? UploadHandler;
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? DownloadHandler;
        public HttpClient CreateClient(string name) => new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/upload/")) { if (UploadHandler is not null) return await UploadHandler(request, ct); if (request.Method == HttpMethod.Put) { UploadPuts++; return Json(new { id = "file-1" }); } var r = Json(new { }); r.Headers.Location = new Uri(UploadLocation); return r; }
            LastFileMethod = request.Method; LastFilePath = path; LastFileBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            FileRequests++; if (FileStatus != HttpStatusCode.OK) return Json(new { error = "provider-secret" }, FileStatus);
            if (request.RequestUri!.Query.Contains("alt=media", StringComparison.Ordinal) && DownloadHandler is not null) return await DownloadHandler(request, ct);
            return Json(new { id = "file-1", name = "report.txt", mimeType = "text/plain", size = "2", version = "1", md5Checksum = "49f68a5c8493ec2c0bf489821c21fc3b", parents = new[] { "root" }, trashed = false });
        }
        private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    }
}
