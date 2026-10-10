using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Browser;

/// <summary>Transport guard, not a browser provider. Resolve once, validate, connect to that exact IP.
/// HTTPS remains end-to-end encrypted. Origin/action authority remains in the native adapter.</summary>
internal sealed class BrowserDestinationProxy(BrowserHostPolicy policy, Func<Uri, bool>? additionalAuthority = null) : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _capacity = new(128);
    private readonly ConcurrentDictionary<long, Connection> _clients = new();
    private readonly ConcurrentDictionary<long, Task> _connections = new();
    private Task? _accept;
    private long _next;
    internal Func<string, CancellationToken, Task<IPAddress[]>> ResolveAsync { get; set; } =
        (host, ct) => Dns.GetHostAddressesAsync(host, ct);
    public string Server => "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _capacity.WaitAsync(_shutdown.Token);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
                catch { _capacity.Release(); throw; }
                var id = Interlocked.Increment(ref _next);
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _clients[id] = new(client, lifetime);
                _connections[id] = completion.Task;
                _ = RunAsync(client, lifetime, id, completion);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task RunAsync(TcpClient client, CancellationTokenSource lifetime, long id, TaskCompletionSource completion)
    {
        try { using (client) await ForwardAsync(client, lifetime.Token); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ArgumentException or ObjectDisposedException) { }
        finally { _clients.TryRemove(id, out _); lifetime.Dispose(); _capacity.Release(); completion.TrySetResult(); _connections.TryRemove(id, out _); }
    }

    private async Task ForwardAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        setup.CancelAfter(TimeSpan.FromSeconds(30));
        var input = client.GetStream();
        using var header = new MemoryStream();
        var one = new byte[1];
        // Read only the header; never consume/replay a request body.
        while (header.Length < 32 * 1024)
        {
            if (await input.ReadAsync(one, setup.Token) == 0) return;
            header.WriteByte(one[0]);
            var bytes = header.GetBuffer(); var n = (int)header.Length;
            if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
        }
        if (header.Length >= 32 * 1024) return;
        var lines = Encoding.Latin1.GetString(header.ToArray()).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3) return;
        var tunnel = first[0] == "CONNECT";
        if (!Uri.TryCreate(tunnel ? "https://" + first[1] : first[1], UriKind.Absolute, out var destination)) return;
        bool Permitted(Uri target) => BrowserTargetPolicy.EvaluateResource(target.AbsoluteUri, policy.NavigationOrigins,
            policy.EffectiveResourceOrigins, policy.PolicyMode).Allowed && (additionalAuthority?.Invoke(target) ?? true);
        // Playwright's request client tunnels HTTP too. CONNECT authorizes a host/port,
        // while the native route owns scheme/origin policy for the enclosed request.
        if (!Permitted(destination) && !(tunnel && Permitted(new UriBuilder(destination)
            { Scheme = "http", Port = destination.Port }.Uri))) return;
        var addresses = await ResolveAsync(destination.DnsSafeHost, setup.Token).WaitAsync(setup.Token);
        // Mixed safe/forbidden answers fail closed. Never hand the hostname to ConnectAsync.
        if (addresses.Length == 0 || addresses.Any(BrowserTargetPolicy.IsForbiddenAddress)) return;
        setup.Token.ThrowIfCancellationRequested();
        if (!Permitted(destination) && !(tunnel && Permitted(new UriBuilder(destination)
            { Scheme = "http", Port = destination.Port }.Uri))) return;
        using var upstream = new TcpClient(addresses[0].AddressFamily);
        await upstream.ConnectAsync(addresses[0], destination.Port, setup.Token);
        var output = upstream.GetStream();
        if (tunnel)
        {
            await input.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), setup.Token);
        }
        else
        {
            // A single HTTP request per connection prevents unvalidated subsequent destinations.
            var forwarded = new StringBuilder(first[0] + " " + destination.PathAndQuery + " " + first[2] + "\r\n");
            var upgrade = lines.Any(line => line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase));
            foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
            {
                if (line.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
                    || !upgrade && line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase)) continue;
                forwarded.Append(line).Append("\r\n");
            }
            if (!upgrade) forwarded.Append("Connection: close\r\n");
            forwarded.Append("\r\n");
            await output.WriteAsync(Encoding.Latin1.GetBytes(forwarded.ToString()), setup.Token);
        }
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var send = input.CopyToAsync(output, relay.Token);
        var receive = output.CopyToAsync(input, relay.Token);
        await Task.WhenAny(send, receive);
        await relay.CancelAsync();
        try { await Task.WhenAll(send, receive); } catch (OperationCanceledException) { }
    }

    internal void FenceConnections()
    {
        foreach (var connection in _clients.Values)
        {
            try { connection.Lifetime.Cancel(); } catch (ObjectDisposedException) { }
            connection.Client.Dispose();
        }
    }

    private sealed record Connection(TcpClient Client, CancellationTokenSource Lifetime);

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Stop();
        FenceConnections();
        if (_accept is not null) await _accept;
        await Task.WhenAll(_connections.Values.ToArray());
        _shutdown.Dispose();
        _capacity.Dispose();
    }
}
