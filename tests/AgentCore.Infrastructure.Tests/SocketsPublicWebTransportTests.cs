using System.Net;
using System.Net.Sockets;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class SocketsPublicWebTransportTests
{
    [Theory] [InlineData("GET")] [InlineData("HEAD")] [InlineData("POST")] [InlineData("DELETE")]
    public async Task Exact_empty_body_Content_Type_reaches_the_wire_after_all_dispatch_checks(string method)
    {
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        try
        {
            var server=Task.Run(async()=> {
                using var client=await listener.AcceptTcpClientAsync();await using var stream=client.GetStream();var captured=new System.Text.StringBuilder();var buffer=new byte[4096];
                while(!captured.ToString().Contains("\r\n\r\n",StringComparison.Ordinal)) { var read=await stream.ReadAsync(buffer);if(read==0)break;captured.Append(System.Text.Encoding.ASCII.GetString(buffer,0,read)); }
                await stream.WriteAsync("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());return captured.ToString();
            });
            var checks=0;var transport=new SocketsPublicWebTransport(new ScriptedDnsResolver(IPAddress.Parse("8.8.8.8")),connect:async(_,_,token)=> {
                var socket=new Socket(AddressFamily.InterNetwork,SocketType.Stream,ProtocolType.Tcp);await socket.ConnectAsync((IPEndPoint)listener.LocalEndpoint,token);return new NetworkStream(socket,true);
            });
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response=await transport.SendAsync(new(method,new Uri("http://public.example/submit"),[new("Content-Type","text/plain")],[]) { AuthorizeDispatch=_=> { checks++;return ValueTask.CompletedTask; } },deadline.Token);
            var wire=await server.WaitAsync(deadline.Token);Assert.Equal(204,response.StatusCode);Assert.StartsWith(method+" /submit HTTP/1.1",wire);Assert.Contains("Content-Type: text/plain",wire,StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Cookie:",wire,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("Authorization:",wire,StringComparison.OrdinalIgnoreCase);Assert.Equal(4,checks);
        }
        finally { listener.Stop(); }
    }
    [Theory] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task Tenant_dispatch_check_refuses_before_HTTP_bytes_after_DNS_and_after_connect(int refuseAt)
    {
        var dns=new ScriptedDnsResolver(IPAddress.Parse("8.8.8.8"),IPAddress.Parse("1.1.1.1"));var checks=0;var connects=0;var stream=new TrackingStream();
        var transport=new SocketsPublicWebTransport(dns,connect:(_,_,_)=> { connects++;return ValueTask.FromResult<Stream>(stream); });
        var outbound=new PublicWebOutboundRequest("POST",new Uri("http://public.example/submit"),[],[1,2,3]) {
            AuthorizeDispatch=_=>++checks==refuseAt?throw new AgentCore.Application.Security.AccessDeniedException("permission_denied"):ValueTask.CompletedTask };
        await Assert.ThrowsAnyAsync<Exception>(()=>transport.SendAsync(outbound,CancellationToken.None).AsTask());
        Assert.Equal(refuseAt,checks);Assert.Equal(refuseAt==4?1:0,connects);Assert.Equal(0,stream.Writes);
        if(refuseAt==4)Assert.True(stream.Disposed);
    }
    private sealed class TrackingStream:MemoryStream
    {
        public int Writes;public bool Disposed;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default) { Writes++;return base.WriteAsync(buffer,ct); }
        protected override void Dispose(bool disposing) { Disposed=true;base.Dispose(disposing); }
    }
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
        var attempts = new List<IPAddress>();
        var transport = new SocketsPublicWebTransport(dns, connect: (address, _, _) =>
        {
            attempts.Add(address);
            throw new SocketException((int)SocketError.ConnectionRefused);
        });
        var failure = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://mixed.example/"), CancellationToken.None).AsTask());
        Assert.Equal("transport_error", failure.Code);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(attempts));
        Assert.Equal(1, dns.ResolveCount);
    }

    [Fact]
    public async Task GetAsync_resolves_dns_on_each_request_for_rebinding_defense()
    {
        var dns = new AlternatingDnsResolver(
            disallowed: IPAddress.Parse("10.0.0.1"),
            allowed: IPAddress.Parse("93.184.216.34"));
        var attempts = new List<IPAddress>();
        var transport = new SocketsPublicWebTransport(dns, connect: (address, _, _) =>
        {
            attempts.Add(address);
            throw new SocketException((int)SocketError.ConnectionRefused);
        });

        var first = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://rebind.example/a"), CancellationToken.None).AsTask());
        Assert.Equal("forbidden_host", first.Code);

        Assert.Empty(attempts);
        var second = await Assert.ThrowsAsync<PublicWebFetchException>(() =>
            transport.GetAsync(new Uri("https://rebind.example/b"), CancellationToken.None).AsTask());
        Assert.Equal("transport_error", second.Code);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(attempts));

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
