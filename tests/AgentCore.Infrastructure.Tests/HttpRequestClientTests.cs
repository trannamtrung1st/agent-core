using AgentCore.Application.Ports;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class HttpRequestClientTests
{
    [Fact]
    public async Task Post_does_not_follow_redirects()
    {
        var transport = new QueueTransport();
        transport.Enqueue(new PublicWebTransportResponse(302, new Uri("https://example.com/next"), null, []));
        IHttpRequestClient client = new HttpRequestClient(transport);
        var result = await client.SendAsync(new HttpToolRequest(
            "POST",
            new Uri("https://example.com/items"),
            [],
            "body"u8.ToArray(),
            FollowRedirects: false));
        Assert.Equal(302, result.StatusCode);
        Assert.Equal("https://example.com/next", result.RedirectLocation);
        Assert.True(result.Untrusted);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal("POST", transport.LastMethod);
        Assert.Equal("body", System.Text.Encoding.UTF8.GetString(transport.LastBody));
    }

    [Fact]
    public async Task Get_follows_a_public_redirect_without_resending_a_body()
    {
        var transport = new QueueTransport();
        transport.Enqueue(new PublicWebTransportResponse(302, new Uri("https://example.com/next"), null, []));
        transport.Enqueue(new PublicWebTransportResponse(200, null, "application/json", """{"ok":true}"""u8.ToArray()));
        IHttpRequestClient client = new HttpRequestClient(transport);
        var result = await client.SendAsync(new HttpToolRequest(
            "GET",
            new Uri("https://example.com/start"),
            [],
            [],
            FollowRedirects: true));
        Assert.Null(result.ErrorCode);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("ok", result.Body, StringComparison.Ordinal);
        Assert.True(result.Untrusted);
        Assert.Equal(2, transport.CallCount);
        Assert.Empty(transport.Bodies[1]);
    }

    [Fact]
    public async Task Get_rejects_a_redirect_to_a_private_host()
    {
        var transport = new QueueTransport();
        transport.Enqueue(new PublicWebTransportResponse(302, new Uri("http://169.254.169.254/latest"), null, []));
        IHttpRequestClient client = new HttpRequestClient(transport);
        var result = await client.SendAsync(new HttpToolRequest(
            "GET",
            new Uri("https://example.com/start"),
            [],
            [],
            FollowRedirects: true));
        Assert.Equal("forbidden_host", result.ErrorCode);
        Assert.Equal(1, transport.CallCount);
    }

    private sealed class QueueTransport : IPublicWebTransport
    {
        private readonly Queue<PublicWebTransportResponse> _responses = new();

        public int CallCount { get; private set; }

        public string? LastMethod { get; private set; }

        public byte[] LastBody { get; private set; } = [];

        public List<byte[]> Bodies { get; } = [];

        public void Enqueue(PublicWebTransportResponse response) => _responses.Enqueue(response);

        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken cancellationToken) =>
            SendAsync(new PublicWebOutboundRequest("GET", uri, [], []), cancellationToken);

        public ValueTask<PublicWebTransportResponse> SendAsync(
            PublicWebOutboundRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastMethod = request.Method;
            LastBody = request.Body;
            Bodies.Add(request.Body);
            return ValueTask.FromResult(_responses.Dequeue());
        }
    }
}
