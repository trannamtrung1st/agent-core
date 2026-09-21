using System.Net;
using System.Net.Sockets;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class PublicWebSocketsHttpFactoryTests
{
    [Fact]
    public void CreateHandler_disables_cookies_auto_redirect_and_decompression()
    {
        var dns = new NoOpDnsResolver();
        using var handler = PublicWebSocketsHttpFactory.CreateHandler(dns);
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }

    [Fact]
    public async Task Connect_tries_next_permitted_address_when_the_first_fails()
    {
        var first = IPAddress.Parse("2001:4860:4860::8888");
        var second = IPAddress.Parse("8.8.8.8");
        var attempts = new List<IPAddress>();
        var stream = await PublicWebSocketsHttpFactory.ConnectFirstPermittedAsync(
            [first, second],
            443,
            (address, _, _) =>
            {
                attempts.Add(address);
                if (address.Equals(first))
                {
                    throw new SocketException((int)SocketError.NetworkUnreachable);
                }

                return ValueTask.FromResult<Stream>(new MemoryStream());
            },
            CancellationToken.None);

        Assert.Equal([first, second], attempts);
        Assert.IsType<MemoryStream>(stream);
    }

    [Fact]
    public async Task Connect_reports_transport_error_when_every_permitted_address_fails()
    {
        var ex = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            PublicWebSocketsHttpFactory.ConnectFirstPermittedAsync(
                [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("1.1.1.1")],
                443,
                (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused),
                CancellationToken.None));

        Assert.Equal("transport_error", ex.Code);
        Assert.NotEqual("forbidden_host", ex.Code);
    }

    [Fact]
    public async Task Connect_reports_forbidden_host_when_no_address_is_permitted()
    {
        var ex = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            PublicWebSocketsHttpFactory.ConnectFirstPermittedAsync(
                [IPAddress.Parse("10.0.0.1"), IPAddress.Parse("127.0.0.1")],
                443,
                (_, _, _) => throw new InvalidOperationException("must not connect"),
                CancellationToken.None));

        Assert.Equal("forbidden_host", ex.Code);
    }

    [Fact]
    public void CreateGetRequest_adds_only_bounded_accept_header()
    {
        using var request = PublicWebSocketsHttpFactory.CreateGetRequest(new Uri("https://example.com/page"));
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(request.Headers.Contains("Cookie"));
        Assert.Contains(
            request.Headers.Accept,
            value => value.MediaType == "text/html" || value.MediaType == "text/plain");
    }

    private sealed class NoOpDnsResolver : IPublicWebDnsResolver
    {
        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Array.Empty<IPAddress>());
    }
}
