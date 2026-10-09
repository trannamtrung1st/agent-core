using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentCore.Application.Tests;

// Test-only, two loopback origins. The protected record is issued only after server-side login.
internal sealed class GenericSsoFixture : IAsyncDisposable
{
    internal const string Email = "demo@example.test";
    internal const string Password = "synthetic-sso-password-63819";
    private readonly HttpListener _application;
    private readonly HttpListener _identity;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, byte> _tickets = new();
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly Task[] _servers;
    internal string ApplicationOrigin => _application.Prefixes.Single().TrimEnd('/');
    internal string IdentityOrigin => _identity.Prefixes.Single().TrimEnd('/');
    internal int AcceptedLogins;
    internal int ProtectedReads;
    internal GenericSsoFixture()
    {
        _application = Listener();
        try { _identity = Listener(); }
        catch { _application.Close(); _stop.Dispose(); throw; }
        _servers = [Serve(_application, false), Serve(_identity, true)];
    }
    private static HttpListener Listener()
    {
        // A released ephemeral port can be taken before HttpListener binds it.
        // Retry fixture setup only; never retry a browser effect or loosen assertions.
        for (var attempt = 0; ; attempt++)
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            socket.Stop();
            try { listener.Start(); return listener; }
            catch (HttpListenerException) when (attempt < 4) { listener.Close(); }
            catch { listener.Close(); throw; }
        }
    }
    private async Task Serve(HttpListener listener, bool identity)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(_stop.Token);
                try { await Reply(context, identity); }
                finally { context.Response.Close(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    private async Task Reply(HttpListenerContext context, bool identity)
    {
        var request = context.Request; var response = context.Response;
        if (request.Url!.AbsolutePath == "/favicon.ico") { response.StatusCode = 204; return; }
        if (identity)
        {
            if (request.HttpMethod == "POST" && request.Url.AbsolutePath == "/session")
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var form = (await reader.ReadToEndAsync()).Split('&').Select(pair => pair.Split('=', 2))
                    .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair.ElementAtOrDefault(1) ?? ""));
                if (form.GetValueOrDefault("email") != Email || form.GetValueOrDefault("password") != Password)
                { response.StatusCode = 401; await Html(response, "<h1>Sign-in failed</h1>"); return; }
                Interlocked.Increment(ref AcceptedLogins);
                var ticket = Guid.NewGuid().ToString("N"); _tickets[ticket] = 0;
                response.Redirect(ApplicationOrigin + "/callback?ticket=" + ticket); return;
            }
            await Html(response, """<!doctype html><title>Demo identity</title><h1>Sign in to Demo records</h1><form method="post" action="/session"><label>Email <input name="email" type="email" autocomplete="username"></label><label>Password <input name="password" type="password" autocomplete="current-password"></label><button type="submit">Sign in</button></form>"""); return;
        }
        if (request.Url.AbsolutePath == "/callback" && request.QueryString["ticket"] is { } code && _tickets.TryRemove(code, out _))
        {
            response.SetCookie(new Cookie("demo-session", _session, "/") { HttpOnly = true });
            response.Redirect(ApplicationOrigin + "/record"); return;
        }
        if (request.Cookies["demo-session"]?.Value != _session)
        { response.Redirect(IdentityOrigin + "/login"); return; }
        Interlocked.Increment(ref ProtectedReads);
        await Html(response, """<!doctype html><title>Demo protected records</title><main><h1>Record AC-1042</h1><p role="status">Authenticated: AC-1042 is Ready for inspection.</p></main>""");
    }
    private static async Task Html(HttpListenerResponse response, string html)
    {
        response.ContentType = "text/html; charset=utf-8"; var bytes = Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = bytes.Length; await response.OutputStream.WriteAsync(bytes);
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _application.Close(); _identity.Close(); await Task.WhenAll(_servers); _stop.Dispose();
    }
}
