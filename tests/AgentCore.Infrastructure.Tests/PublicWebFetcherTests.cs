using System.Net;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class PublicWebFetcherTests
{
    [Fact]
    public async Task Fetch_succeeds_for_public_https_text_and_strips_html_noise()
    {
        var transport = new FakeTransport();
        transport.Enqueue(
            new Uri("https://example.com/page"),
            new PublicWebTransportResponse(
                200,
                null,
                "text/html",
                "<html><head><script>alert(1)</script><style>.x{}</style></head><body><p>Hello</p></body></html>"u8.ToArray()));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/page")));
        Assert.Null(result.ErrorCode);
        Assert.Equal("Hello", result.Text);
        Assert.DoesNotContain("alert", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_follows_redirect_when_target_is_public()
    {
        var transport = new FakeTransport();
        transport.Enqueue(
            new Uri("https://example.com/a"),
            new PublicWebTransportResponse(302, new Uri("https://example.com/b"), null, []));
        transport.Enqueue(
            new Uri("https://example.com/b"),
            new PublicWebTransportResponse(200, null, "text/plain", "ok"u8.ToArray()));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/a")));
        Assert.Equal("ok", result.Text);
        Assert.Equal("https://example.com/b", result.FinalUrl);
    }

    [Fact]
    public async Task Fetch_rejects_redirect_to_private_host()
    {
        var transport = new FakeTransport();
        transport.Enqueue(
            new Uri("https://example.com/a"),
            new PublicWebTransportResponse(302, new Uri("http://127.0.0.1/secret"), null, []));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/a")));
        Assert.Equal("forbidden_host", result.ErrorCode);
        Assert.Equal(1, transport.CallCount);
    }

    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[::1]/")]
    [InlineData("ftp://example.com/")]
    [InlineData("http://user:pass@example.com/")]
    public async Task Fetch_rejects_unsafe_urls_without_transport(string url)
    {
        var transport = new FakeTransport();
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri(url)));
        Assert.NotNull(result.ErrorCode);
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task Fetch_rejects_oversized_body()
    {
        var transport = new FakeTransport();
        transport.Enqueue(
            new Uri("https://example.com/big"),
            new PublicWebTransportResponse(200, null, "text/plain", new byte[PublicWebLimits.MaxBodyBytes + 1]));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/big")));
        Assert.Equal("body_too_large", result.ErrorCode);
    }

    [Fact]
    public async Task Fetch_projects_text_to_256_kib()
    {
        var transport = new FakeTransport();
        var body = new string('a', PublicWebLimits.MaxProjectedTextChars + 10);
        transport.Enqueue(
            new Uri("https://example.com/long"),
            new PublicWebTransportResponse(200, null, "text/plain", Encoding.UTF8.GetBytes(body)));
        IPublicWebFetcher fetcher = new PublicWebFetcher(transport);
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/long")));
        Assert.True(result.Truncated);
        Assert.Equal(PublicWebLimits.MaxProjectedTextChars, result.Text.Length);
    }

    [Fact]
    public async Task Synthetic_search_returns_deterministic_bounded_results()
    {
        var provider = new SyntheticWebSearchProvider();
        Assert.True(provider.IsAvailable);
        var result = await provider.SearchAsync(new WebSearchRequest("agent core", 3));
        Assert.Equal(3, result.Results.Count);
        Assert.All(result.Results, item => Assert.StartsWith("https://example.test/", item.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void Public_address_policy_rejects_private_literals()
    {
        Assert.False(PublicAddressPolicy.IsAllowed(IPAddress.Parse("127.0.0.1")));
        Assert.False(PublicAddressPolicy.IsAllowed(IPAddress.Parse("10.1.2.3")));
        Assert.False(PublicAddressPolicy.IsAllowed(IPAddress.Parse("169.254.169.254")));
        Assert.True(PublicAddressPolicy.IsAllowed(IPAddress.Parse("93.184.216.34")));
    }

    private sealed class FakeTransport : IPublicWebTransport
    {
        private readonly Queue<(Uri Uri, PublicWebTransportResponse Response)> _responses = new();

        public int CallCount { get; private set; }

        public void Enqueue(Uri uri, PublicWebTransportResponse response) => _responses.Enqueue((uri, response));

        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake response queued.");
            }

            var (expected, response) = _responses.Dequeue();
            Assert.Equal(expected, uri);
            return ValueTask.FromResult(response);
        }
    }
}
