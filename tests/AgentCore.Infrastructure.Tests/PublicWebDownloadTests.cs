using System.Net;
using System.Net.Http;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class PublicWebDownloadTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("application/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("application/x-arbitrary")]
    public async Task Download_never_decodes_bytes_and_uses_its_own_bound(string? mime)
    {
        var body = new byte[PublicWebLimits.MaxBodyBytes + 1]; body[0] = 255; body[1] = 0; body[^1] = 128;
        var transport = new Fixture(new(200, null, mime, body, "attachment; filename*=UTF-8''form.docx"));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var downloaded = await fetcher.DownloadAsync(new(new("https://official.example/file")));
        Assert.Null(downloaded.ErrorCode); Assert.Equal(body, downloaded.Bytes); Assert.Equal("form.docx", downloaded.SuggestedFileName);
        Assert.Equal(10 * 1024 * 1024, Assert.Single(transport.Requests).MaxResponseBytes);
        Assert.DoesNotContain(transport.Requests[0].Headers, h => h.Name is "Authorization" or "Cookie");
        var fetched = await fetcher.FetchAsync(new(new("https://official.example/file"))); Assert.Equal("body_too_large", fetched.ErrorCode);
    }

    [Theory]
    [InlineData("http://127.0.0.1/secret")]
    [InlineData("http://[::ffff:10.0.0.1]/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://metadata.google.internal/")]
    [InlineData("http://localhost/")]
    [InlineData("ftp://official.example/")]
    [InlineData("https://user:secret@official.example/")]
    public async Task Unsafe_initial_and_redirect_targets_never_dispatch(string target)
    {
        var transport = new Fixture(new(302, new(target), null, [])); IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        Assert.NotNull((await fetcher.DownloadAsync(new(new(target)))).ErrorCode); Assert.Empty(transport.Requests);
        var redirect = await fetcher.DownloadAsync(new(new("https://official.example/file")));
        Assert.NotNull(redirect.ErrorCode); Assert.Single(transport.Requests); Assert.Empty(redirect.Bytes);
    }

    [Fact]
    public async Task Redirect_hops_and_dns_policy_are_preserved_for_binary_downloads()
    {
        var redirects = new Fixture(new(302, new("https://official.example/loop"), null, [])); IPublicWebFetcher fetcher = new PublicWebFetcher(redirects);
        Assert.Equal("too_many_redirects", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode);
        Assert.Equal(PublicWebLimits.MaxRedirects + 1, redirects.Requests.Count);
        var dns = new PrivateDns(); fetcher = new PublicWebFetcher(new SocketsPublicWebTransport(dns));
        Assert.Equal("forbidden_host", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode); Assert.Equal(1, dns.Calls);
    }

    [Theory]
    [InlineData(206)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Partial_or_inaccessible_responses_never_report_completion(int status)
    {
        IPublicWebFetcher fetcher = new PublicWebFetcher(new Fixture(new(status, null, "text/html", "Login"u8.ToArray())));
        var result = await fetcher.DownloadAsync(new(new("https://official.example/file"))); Assert.Equal("http_error", result.ErrorCode); Assert.Empty(result.Bytes); Assert.Contains("login", result.ErrorMessage);
    }

    [Fact]
    public async Task Oversize_encoding_transport_failure_timeout_and_cancellation_are_truthful()
    {
        IPublicWebFetcher fetcher = new PublicWebFetcher(new Fixture(new(200, null, null, new byte[11])), maxDownloadBytes: 10);
        Assert.Equal("body_too_large", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode);
        fetcher = new PublicWebFetcher(new Fixture(new(200, null, null, [1,2], ContentEncoding: "gzip")));
        Assert.Equal("unsupported_content_encoding", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode);
        fetcher = new PublicWebFetcher(new FailureTransport()); Assert.Equal("transport_error", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode);
        var holding = new HoldingTransport(); fetcher = new PublicWebFetcher(holding, downloadTimeoutSeconds: 1);
        Assert.Equal("timeout", (await fetcher.DownloadAsync(new(new("https://official.example/file")))).ErrorCode);
        using var cancellation = new CancellationTokenSource();
        var pending = fetcher.DownloadAsync(new(new("https://official.example/file")), cancellation.Token).AsTask();
        await holding.Dispatched.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Transport_bounds_declared_and_chunked_content_before_accepting_the_body(bool declared)
    {
        using var content = declared ? (HttpContent)new ByteArrayContent(new byte[101]) : new StreamContent(new NonSeekableStream(new byte[101]));
        if (declared) Assert.Equal(101, content.Headers.ContentLength); else Assert.Null(content.Headers.ContentLength);
        var error = await Assert.ThrowsAsync<PublicWebFetchException>(() => SocketsPublicWebTransport.ReadBoundedBodyAsync(content, 100, default));
        Assert.Equal("body_too_large", error.Code);
        using var exact = new StreamContent(new NonSeekableStream([0,255,128]));
        Assert.Equal(new byte[] {0,255,128}, await SocketsPublicWebTransport.ReadBoundedBodyAsync(exact, 3, default));
    }

    [Fact]
    public async Task Transport_body_read_observes_cancellation_and_disposes_the_response_stream()
    {
        var stream = new HoldingStream(); using var content = new StreamContent(stream); using var cancellation = new CancellationTokenSource();
        var pending = SocketsPublicWebTransport.ReadBoundedBodyAsync(content, 100, cancellation.Token);
        await stream.Dispatched.Task; cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.True(stream.Disposed);
    }

    [Fact]
    public void Download_configuration_rejects_unbounded_or_invalid_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicWebFetcher(new Fixture(new(200,null,null,[])), maxDownloadBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicWebFetcher(new Fixture(new(200,null,null,[])), maxDownloadBytes: 50*1024*1024+1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PublicWebFetcher(new Fixture(new(200,null,null,[])), downloadTimeoutSeconds: 31));
    }

    private sealed class Fixture(PublicWebTransportResponse response) : IPublicWebTransport
    {
        public List<PublicWebOutboundRequest> Requests { get; } = [];
        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken ct) => ValueTask.FromResult(response);
        public ValueTask<PublicWebTransportResponse> SendAsync(PublicWebOutboundRequest request, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Requests.Add(request); return ValueTask.FromResult(response); }
    }
    private sealed class PrivateDns : IPublicWebDnsResolver
    { public int Calls; public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken ct) { Calls++; return ValueTask.FromResult(new[] { IPAddress.Parse("10.0.0.1"), IPAddress.Parse("::ffff:127.0.0.1") }); } }
    private sealed class FailureTransport : IPublicWebTransport
    { public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken ct) => throw new IOException("private transport details"); public ValueTask<PublicWebTransportResponse> SendAsync(PublicWebOutboundRequest request, CancellationToken ct) => GetAsync(request.Uri, ct); }
    private sealed class HoldingTransport : IPublicWebTransport
    {
        public TaskCompletionSource Dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken ct) => SendAsync(new("GET",uri,[],[]),ct);
        public async ValueTask<PublicWebTransportResponse> SendAsync(PublicWebOutboundRequest request, CancellationToken ct)
        { Dispatched.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan,ct); throw new InvalidOperationException(); }
    }
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    { public override bool CanSeek => false; }
    private sealed class HoldingStream : MemoryStream
    {
        public TaskCompletionSource Dispatched = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Disposed;
        public override bool CanSeek => false;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Dispatched.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan,ct); return 0; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
