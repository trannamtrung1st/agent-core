using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class SocketsPublicWebTransportTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    public async Task GetAsync_when_dns_returns_only_private_ipv4_throws_forbidden_host(string privateAddress)
    {
        var dns = new ScriptedDnsResolver(IPAddress.Parse(privateAddress));
        var transport = new SocketsPublicWebTransport(dns);

        var ex = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://blocked.example/page"), CancellationToken.None).AsTask());

        Assert.Equal("forbidden_host", ex.Code);
        Assert.Equal(1, dns.ResolveCount);
    }

    [Fact]
    public async Task GetAsync_when_dns_returns_ipv4_mapped_private_throws_forbidden_host()
    {
        var mapped = IPAddress.Parse("::ffff:10.0.0.1");
        var dns = new ScriptedDnsResolver(mapped);
        var transport = new SocketsPublicWebTransport(dns);

        var ex = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://mapped-private.example/"), CancellationToken.None).AsTask());

        Assert.Equal("forbidden_host", ex.Code);
    }

    [Theory]
    [InlineData("fd12:3456:789a:1::1")]
    [InlineData("fe80::1")]
    public async Task GetAsync_when_dns_returns_non_public_ipv6_throws_forbidden_host(string ipv6)
    {
        var dns = new ScriptedDnsResolver(IPAddress.Parse(ipv6));
        var transport = new SocketsPublicWebTransport(dns);

        var ex = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://ula.example/"), CancellationToken.None).AsTask());

        Assert.Equal("forbidden_host", ex.Code);
    }

    [Fact]
    public async Task GetAsync_when_dns_returns_public_then_private_only_attempts_permitted_address()
    {
        var dns = new ScriptedDnsResolver(
            IPAddress.Parse("10.0.0.1"),
            IPAddress.Parse("93.184.216.34"));
        var transport = new SocketsPublicWebTransport(dns);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transport.GetAsync(new Uri("https://mixed.example/"), CancellationToken.None).AsTask());

        Assert.Equal(1, dns.ResolveCount);
    }

    [Fact]
    public async Task GetAsync_resolves_dns_on_each_request_for_rebinding_defense()
    {
        var dns = new AlternatingDnsResolver(
            disallowed: IPAddress.Parse("10.0.0.1"),
            allowed: IPAddress.Parse("93.184.216.34"));
        var transport = new SocketsPublicWebTransport(dns);

        var first = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://rebind.example/a"), CancellationToken.None).AsTask());
        Assert.Equal("forbidden_host", first.Code);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            transport.GetAsync(new Uri("https://rebind.example/b"), CancellationToken.None).AsTask());

        Assert.Equal(2, dns.ResolveCount);
    }

    [Fact]
    public async Task Fetcher_maps_transport_forbidden_host_to_bounded_result()
    {
        var dns = new ScriptedDnsResolver(IPAddress.Parse("127.0.0.1"));
        IPublicWebFetcher fetcher = new PublicWebFetcher(new SocketsPublicWebTransport(dns));
        var result = await fetcher.FetchAsync(new PublicWebFetchRequest(new Uri("https://example.com/")));
        Assert.Equal("forbidden_host", result.ErrorCode);
    }

    private sealed class ScriptedDnsResolver : IPublicWebDnsResolver
    {
        private readonly IPAddress[] _addresses;

        public ScriptedDnsResolver(params IPAddress[] addresses) => _addresses = addresses;

        public int ResolveCount { get; private set; }

        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            ResolveCount++;
            return ValueTask.FromResult(_addresses);
        }
    }

    private sealed class AlternatingDnsResolver : IPublicWebDnsResolver
    {
        private readonly IPAddress _disallowed;
        private readonly IPAddress _allowed;

        public AlternatingDnsResolver(IPAddress disallowed, IPAddress allowed)
        {
            _disallowed = disallowed;
            _allowed = allowed;
        }

        public int ResolveCount { get; private set; }

        public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            ResolveCount++;
            var addresses = ResolveCount == 1 ? new[] { _disallowed } : new[] { _allowed };
            return ValueTask.FromResult(addresses);
        }
    }
}
