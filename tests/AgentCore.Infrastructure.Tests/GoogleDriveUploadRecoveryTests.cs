using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Drive;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class GoogleDriveTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(GoogleDriveProvider.UploadChunkBytes)]
    [InlineData(50 * 1024 * 1024)]
    public async Task Empty_aligned_and_maximum_size_uploads_keep_each_request_bounded(int size)
    {
        using var f = new Fixture(); var (provider, operation) = await UploadFixture(f); var bytes = new byte[size];
        var accepted = 0; var puts = 0;
        f.Transport.UploadHandler = (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(StartedUpload());
            puts++; var range = request.Content!.Headers.ContentRange!;
            Assert.Equal(size, range.Length);
            Assert.InRange(request.Content.Headers.ContentLength!.Value, 0, GoogleDriveProvider.UploadChunkBytes);
            if (size > 0) { Assert.Equal(accepted, range.From); accepted = checked((int)range.To!.Value + 1); }
            return Task.FromResult(Progress(accepted, size));
        };
        await provider.UploadAsync("file-1", "report.bin", "root", "application/octet-stream", bytes, operation, default);
        Assert.Equal(size, accepted); Assert.Equal(Math.Max(1, (size + GoogleDriveProvider.UploadChunkBytes - 1) / GoogleDriveProvider.UploadChunkBytes), puts);
    }
    private static HttpResponseMessage StartedUpload()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new("https://www.googleapis.com/upload/drive/v3/files?upload_id=opaque");
        return response;
    }
    private static HttpResponseMessage Progress(int accepted, int total)
    {
        var response = new HttpResponseMessage(accepted == total ? HttpStatusCode.OK : (HttpStatusCode)308);
        if (accepted != total && accepted > 0) response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{accepted - 1}");
        return response;
    }
    private static async Task<(GoogleDriveProvider Provider, DriveOperation Operation)> UploadFixture(Fixture f)
    {
        var account = await f.Credentials.CreateGoogleDriveAccountAsync("Upload recovery");
        await f.Complete(await f.Start(account));
        var operation = new DriveOperation(Guid.NewGuid(), f.Owner, account.CredentialId, "hash", 1,
            "drive.upload", "file-1", "{}", "Dispatched", 1, DateTimeOffset.UtcNow);
        await f.Operations.ClaimAsync(operation, default);
        return (f.Provider(account.CredentialId), operation);
    }

    [Fact]
    public async Task Lost_chunk_inspects_accepted_range_then_sends_only_the_remaining_bytes()
    {
        using var f = new Fixture(); var (provider, operation) = await UploadFixture(f);
        var bytes = Enumerable.Range(0, GoogleDriveProvider.UploadChunkBytes * 2 + 17).Select(i => (byte)i).ToArray();
        var accepted = 0; var starts = 0; var probes = 0; var puts = new List<int>();
        f.Transport.UploadHandler = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) { starts++; return StartedUpload(); }
            var range = request.Content!.Headers.ContentRange!;
            if (range.From is null) { probes++; Assert.Equal(bytes.Length, range.Length); Assert.Empty(await request.Content.ReadAsByteArrayAsync(ct)); return Progress(accepted, bytes.Length); }
            Assert.Equal(accepted, range.From); Assert.Equal(bytes.Length, range.Length);
            var content = await request.Content.ReadAsByteArrayAsync(ct);
            Assert.InRange(content.Length, 1, GoogleDriveProvider.UploadChunkBytes);
            Assert.Equal(bytes.AsSpan(accepted, content.Length).ToArray(), content);
            puts.Add(accepted);
            if (puts.Count == 1) { accepted = 256 * 1024; throw new HttpRequestException("lost partial chunk response"); }
            accepted += content.Length; return Progress(accepted, bytes.Length);
        };
        await provider.UploadAsync("file-1", "report.bin", "root", "application/octet-stream", bytes, operation, default);
        Assert.Equal(1, starts); Assert.Equal(1, probes); Assert.Equal([0, 262144, 1310720], puts);
        Assert.Equal(bytes.Length, accepted);
        var stored = (await f.Operations.GetAsync(f.Owner, operation.OperationId, default))!;
        Assert.DoesNotContain("opaque", stored.ProtectedUploadSession);
    }

    [Fact]
    public async Task Cancellation_then_adapter_restart_reuses_protected_session_and_inspects_before_continuing()
    {
        using var f = new Fixture(); var (provider, operation) = await UploadFixture(f);
        var bytes = new byte[GoogleDriveProvider.UploadChunkBytes + 19]; var starts = 0; var probes = 0; var accepted = 0;
        using var cancellation = new CancellationTokenSource();
        f.Transport.UploadHandler = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) { starts++; return StartedUpload(); }
            var range = request.Content!.Headers.ContentRange!;
            if (range.From is null) { probes++; return Progress(accepted, bytes.Length); }
            Assert.Equal(accepted, range.From);
            if (accepted == 0) { accepted = 256 * 1024; cancellation.Cancel(); throw new OperationCanceledException(ct); }
            accepted += (await request.Content.ReadAsByteArrayAsync(ct)).Length; return Progress(accepted, bytes.Length);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.UploadAsync("file-1", "report.bin", "root", "application/octet-stream", bytes, operation, cancellation.Token).AsTask());
        var saved = (await f.Operations.GetAsync(f.Owner, operation.OperationId, default))!;
        provider = new(saved.CredentialId, 2, f.NewService(), f.Transport, f.Operations, f.Protector, TimeProvider.System);
        await provider.UploadAsync("file-1", "report.bin", "root", "application/octet-stream", bytes, saved, default);
        Assert.Equal(1, starts); Assert.Equal(1, probes); Assert.Equal(bytes.Length, accepted);
    }

    [Fact]
    public async Task Lost_final_response_is_inspected_as_complete_without_another_content_put()
    {
        using var f = new Fixture(); var (provider, operation) = await UploadFixture(f); var writes = 0; var probes = 0;
        f.Transport.UploadHandler = (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(StartedUpload());
            if (request.Content!.Headers.ContentRange!.From is null) { probes++; return Task.FromResult(Progress(2, 2)); }
            writes++; throw new HttpRequestException("lost completed response");
        };
        await provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), operation, default);
        Assert.Equal(1, writes); Assert.Equal(1, probes);
    }

    [Theory]
    [InlineData("bytes=1-2")]
    [InlineData("bytes=0-2")]
    [InlineData("bytes=0--1")]
    [InlineData("bytes=0-1")]
    public async Task Invalid_saved_session_offset_never_sends_content_or_creates_a_session(string range)
    {
        using var f = new Fixture(); var (provider, op) = await UploadFixture(f);
        op = await f.Operations.UpdateAsync(op with { Revision = 2, ProtectedUploadSession = f.Protector.Protect(op.CredentialId, 1, StartedUpload().Headers.Location!.AbsoluteUri) }, 1, default);
        var probes = 0;
        f.Transport.UploadHandler = (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method); Assert.Null(request.Content!.Headers.ContentRange!.From); probes++;
            var response = new HttpResponseMessage((HttpStatusCode)308); response.Headers.TryAddWithoutValidation("Range", range); return Task.FromResult(response);
        };
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal("drive_provider_response_invalid", error.Code); Assert.Equal(1, probes);
    }

    [Fact]
    public async Task Expired_session_stays_uncertain_and_never_starts_a_replacement_upload()
    {
        using var f = new Fixture(); var (provider, op) = await UploadFixture(f);
        op = await f.Operations.UpdateAsync(op with { Revision = 2, ProtectedUploadSession = f.Protector.Protect(op.CredentialId, 1, StartedUpload().Headers.Location!.AbsoluteUri) }, 1, default);
        f.Transport.UploadHandler = (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method); Assert.Null(request.Content!.Headers.ContentRange!.From);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        };
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal("drive_upload_session_expired", error.Code);
        Assert.Null((await f.Operations.GetAsync(f.Owner, op.OperationId, default))!.ProtectedUploadSession);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_progress_and_repeated_transport_loss_are_bounded(bool transportLoss)
    {
        using var f = new Fixture(); var (provider, op) = await UploadFixture(f); var puts = 0;
        f.Transport.UploadHandler = (request, ct) =>
        {
            if (request.Method == HttpMethod.Post) return Task.FromResult(StartedUpload());
            puts++;
            if (transportLoss && request.Content!.Headers.ContentRange!.From is not null) throw new HttpRequestException("lost chunk");
            return Task.FromResult(Progress(0, 2));
        };
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal(transportLoss ? "drive_provider_unavailable" : "drive_effect_indeterminate", error.Code);
        Assert.Equal(transportLoss ? 7 : 4, puts);
    }

    [Fact]
    public async Task Explicit_initial_rejection_proves_non_execution_but_transport_failure_does_not()
    {
        using var f = new Fixture(); var (provider, op) = await UploadFixture(f);
        f.Transport.UploadHandler = (request, ct) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var rejected = await Assert.ThrowsAsync<DriveEffectNotExecutedException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal("drive_scope_limited", rejected.Code);
        f.Transport.UploadHandler = (request, ct) => throw new HttpRequestException("provider secret");
        var unknown = await Assert.ThrowsAsync<AgentCoreException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal("drive_provider_unavailable", unknown.Code); Assert.DoesNotContain("provider secret", unknown.Message);
        Assert.Null((await f.Operations.GetAsync(f.Owner, op.OperationId, default))!.ProtectedUploadSession);
    }

    [Fact]
    public async Task Rejected_authorization_and_cancellation_before_initial_request_prove_non_execution()
    {
        using var f = new Fixture(); var (provider, op) = await UploadFixture(f); var requests = 0;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        f.Transport.UploadHandler = (request, ct) => { requests++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)); };
        var cancellation = await Assert.ThrowsAsync<DriveEffectNotExecutedException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, cancelled.Token).AsTask());
        Assert.Equal("drive_cancelled_before_dispatch", cancellation.Code); Assert.Equal(0, requests);
        var rejection = await Assert.ThrowsAsync<DriveEffectNotExecutedException>(() => provider.UploadAsync("file-1", "report.txt", "root", "text/plain", "hi"u8.ToArray(), op, default).AsTask());
        Assert.Equal("drive_access_revoked", rejection.Code); Assert.Equal(2, requests);
    }
}
