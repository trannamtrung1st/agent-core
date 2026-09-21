using System.Net;
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
