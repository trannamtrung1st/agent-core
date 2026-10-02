using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AgentCore.Infrastructure.Browser;

internal sealed class LoopbackBrowserFixtureHost : IAsyncDisposable
{
    public const string IndexResource = "AgentCore.Infrastructure.Browser.Fixture.index.html";
    public const string NoMatchResource = "AgentCore.Infrastructure.Browser.Fixture.no-match.html";
    public const string RecordResource = "AgentCore.Infrastructure.Browser.Fixture.record.html";
    public const string IsolateResource = "AgentCore.Infrastructure.Browser.Fixture.isolate.html";
    public const string ChallengeResource = "AgentCore.Infrastructure.Browser.Fixture.challenge.html";
    public const string LoginResource = "AgentCore.Infrastructure.Browser.Fixture.login.html";
    public const string SignupResource = "AgentCore.Infrastructure.Browser.Fixture.signup.html";
    public const string AccountResource = "AgentCore.Infrastructure.Browser.Fixture.account.html";
    public const string HiddenAuthResource = "AgentCore.Infrastructure.Browser.Fixture.hidden-auth.html";
    public const string OfflineCaptchaShellResource = "AgentCore.Infrastructure.Browser.Fixture.offline-captcha-shell.html";
    public const string LoginBelowFoldResource = "AgentCore.Infrastructure.Browser.Fixture.login-below-fold.html";
    public const string IdentityResource = "AgentCore.Infrastructure.Browser.Fixture.identity.html";

    private static readonly string[] RequiredResources =
    [
        IndexResource,
        NoMatchResource,
        RecordResource,
        IsolateResource,
        ChallengeResource,
        LoginResource,
        SignupResource,
        AccountResource,
        HiddenAuthResource,
        OfflineCaptchaShellResource,
        LoginBelowFoldResource,
        IdentityResource
    ];

    private readonly ILogger _logger;
    private readonly Func<string, Stream?> _openResource;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _delayEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HttpListener? _listener;
    private Task? _loop;

    public LoopbackBrowserFixtureHost(ILogger logger, Func<string, Stream?>? openResource = null)
    {
        _logger = logger;
        _openResource = openResource ?? OpenEmbedded;
    }

    public bool IsAvailable { get; private set; }

    public int? Port { get; private set; }

    public string? Origin { get; private set; }

    public Task DelayEntered => _delayEntered.Task;

    public Task StartAsync(int fixturePort, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var name in RequiredResources)
        {
            var stream = _openResource(name);
            if (stream is null)
            {
                MarkUnavailable("missing_page");
                return Task.CompletedTask;
            }

            stream.Dispose();
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var port = fixturePort == 0 ? BindEphemeralPort() : fixturePort;
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;
                Origin = $"http://127.0.0.1:{port}";
                IsAvailable = true;
                _loop = Task.Run(() => ListenAsync(_shutdown.Token), CancellationToken.None);
                return Task.CompletedTask;
            }
            catch (HttpListenerException) when (fixturePort == 0 && attempt < 4)
            {
            }
            catch (HttpListenerException)
            {
                MarkUnavailable("bind_failed");
                return Task.CompletedTask;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MarkUnavailable("bind_failed");
                return Task.CompletedTask;
            }
        }

        MarkUnavailable("bind_failed");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            _listener?.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
        {
        }

        _shutdown.Dispose();
    }

    private void MarkUnavailable(string reason)
    {
        IsAvailable = false;
        _logger.LogWarning("Browser fixture is unavailable ({Reason}).", reason);
    }

    private static int BindEphemeralPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (string.Equals(path, "/delay", StringComparison.Ordinal))
            {
                _delayEntered.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return;
            }

            if (string.Equals(path, "/redirect-out", StringComparison.Ordinal))
            {
                context.Response.StatusCode = 302;
                context.Response.RedirectLocation = "https://example.invalid/escape";
                return;
            }

            if (string.Equals(path, "/search", StringComparison.Ordinal))
            {
                var query = context.Request.QueryString["q"];
                if (string.Equals(query, "AC-1042", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/records/AC-1042";
                    return;
                }

                await WriteResourceAsync(context, NoMatchResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/records/AC-1042", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, RecordResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/isolate", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, IsolateResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/challenge", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, ChallengeResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/login", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, LoginResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/signup", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, SignupResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/account", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, AccountResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/hidden-auth", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, HiddenAuthResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/offline-captcha-shell.html", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, OfflineCaptchaShellResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/login-below-fold", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, LoginBelowFoldResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/identity", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, IdentityResource, 200).ConfigureAwait(false);
                return;
            }

            if (path is "/" or "/index.html")
            {
                await WriteResourceAsync(context, IndexResource, 200).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = 404;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
            {
            }
        }
    }

    private async Task WriteResourceAsync(HttpListenerContext context, string resourceName, int statusCode)
    {
        var stream = _openResource(resourceName);
        if (stream is null)
        {
            context.Response.StatusCode = 404;
            return;
        }

        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var html = await reader.ReadToEndAsync().ConfigureAwait(false);
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
    }

    private static Stream? OpenEmbedded(string name) =>
        typeof(LoopbackBrowserFixtureHost).Assembly.GetManifestResourceStream(name);
}
