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
    public const string BudgetWorkflowResource = "AgentCore.Infrastructure.Browser.Fixture.budget-workflow.html";
    public const string CredentialLoginResource = "AgentCore.Infrastructure.Browser.Fixture.credential-login.html";
    public const string LoginResource = "AgentCore.Infrastructure.Browser.Fixture.login.html";
    public const string SignupResource = "AgentCore.Infrastructure.Browser.Fixture.signup.html";
    public const string AccountResource = "AgentCore.Infrastructure.Browser.Fixture.account.html";
    public const string HiddenAuthResource = "AgentCore.Infrastructure.Browser.Fixture.hidden-auth.html";
    public const string OfflineCaptchaShellResource = "AgentCore.Infrastructure.Browser.Fixture.offline-captcha-shell.html";
    public const string LoginBelowFoldResource = "AgentCore.Infrastructure.Browser.Fixture.login-below-fold.html";
    public const string IdentityResource = "AgentCore.Infrastructure.Browser.Fixture.identity.html";
    public const string UploadResource = "AgentCore.Infrastructure.Browser.Fixture.upload.html";
    public const string ControlsResource = "AgentCore.Infrastructure.Browser.Fixture.controls.html";
    public const string StateResource = "AgentCore.Infrastructure.Browser.Fixture.state.html";
    public const string SettleDelayedResource = "AgentCore.Infrastructure.Browser.Fixture.settle-delayed.html";
    public const string SettleClickResource = "AgentCore.Infrastructure.Browser.Fixture.settle-click.html";
    public const string SettlePendingResource = "AgentCore.Infrastructure.Browser.Fixture.settle-pending.html";
    public const string SettleChurnResource = "AgentCore.Infrastructure.Browser.Fixture.settle-churn.html";

    private static readonly string[] RequiredResources =
    [
        IndexResource,
        NoMatchResource,
        RecordResource,
        IsolateResource,
        ChallengeResource,
        LoginResource,
        CredentialLoginResource,
        SignupResource,
        AccountResource,
        HiddenAuthResource,
        OfflineCaptchaShellResource,
        LoginBelowFoldResource,
        IdentityResource,
        UploadResource,
        ControlsResource,
        StateResource,
        SettleDelayedResource,
        SettleClickResource,
        SettlePendingResource,
        SettleChurnResource
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

            if (string.Equals(path, "/budget-workflow", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, BudgetWorkflowResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/credential-login", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, CredentialLoginResource, 200).ConfigureAwait(false);
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

            if (string.Equals(path, "/upload", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, UploadResource, 200).ConfigureAwait(false);
                return;
            }

            if (path is "/browser-native.html" or "/browser-native-frame.html" or "/browser-dense.html" or "/browser-custom-tree.html" or "/adaptive-browser.html")
            {
                await WriteResourceAsync(context, "AgentCore.Infrastructure.Browser.Fixture." + path[1..], 200).ConfigureAwait(false);
                return;
            }
            if (path == "/browser-native-data")
            {
                var bytes = Encoding.UTF8.GetBytes("Async fixture loaded");
                context.Response.ContentType = "text/plain";
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                return;
            }
            if (path == "/browser-native-worker.js")
            {
                context.Response.ContentType = "application/javascript";
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("self.addEventListener('fetch', event => event.respondWith(new Response('worker intercepted')));"));
                return;
            }
            if (path == "/browser-native-download")
            {
                context.Response.ContentType = "text/plain";
                context.Response.AddHeader("Content-Disposition", "attachment; filename=browser-note.txt");
                await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("Generic browser fixture note.")).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/controls", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, ControlsResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/state", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, StateResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settle-hold", StringComparison.Ordinal))
            {
                await WriteSettleHoldAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settle-delayed", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, SettleDelayedResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settle-click", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, SettleClickResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settle-pending", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, SettlePendingResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/settle-churn", StringComparison.Ordinal))
            {
                await WriteResourceAsync(context, SettleChurnResource, 200).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/files/notes.csv", StringComparison.Ordinal))
            {
                await WriteAttachmentAsync(
                    context,
                    "text/csv; charset=utf-8",
                    "notes.csv",
                    "sku,name\r\nAC-1042,Keyboard\r\n").ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/files/payload.exe", StringComparison.Ordinal))
            {
                await WriteAttachmentAsync(
                    context,
                    "application/octet-stream",
                    "payload.exe",
                    "MZ-not-allowed").ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/files/oversized.pdf", StringComparison.Ordinal))
            {
                await WriteChunkedOversizedAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.Equals(path, "/bounce", StringComparison.Ordinal))
            {
                var bytes = Encoding.UTF8.GetBytes(
                    "<!doctype html><meta charset=\"utf-8\"><script>location.replace('/');</script>");
                context.Response.StatusCode = 200;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
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

    private static async Task WriteSettleHoldAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var ms = 500;
        if (int.TryParse(context.Request.QueryString["ms"], out var parsed))
        {
            ms = Math.Clamp(parsed, 0, 5000);
        }

        var token = context.Request.QueryString["token"];
        if (token is not ("AC-SETTLE-ROW" or "AC-SETTLE-RESULT" or "AC-SETTLE-LATE"))
        {
            token = "AC-SETTLE-ROW";
        }

        try
        {
            await Task.Delay(ms, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(token);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    internal const int ChunkedOversizedTailBytes = 2 * 1024 * 1024;

    internal const int ChunkedOversizedTotalBytes = (5 * 1024 * 1024) + ChunkedOversizedTailBytes;

    internal long ChunkedAttachmentBytesWritten { get; private set; }

    internal int ChunkedAttachmentRequests { get; private set; }

    private async Task WriteChunkedOversizedAsync(HttpListenerContext context)
    {
        var total = ChunkedOversizedTotalBytes;
        var written = 0;
        ChunkedAttachmentRequests++;
        try
        {
            context.Response.SendChunked = true;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/pdf";
            context.Response.Headers["Content-Disposition"] = "attachment; filename=\"oversized.pdf\"";
            var chunk = new byte[64 * 1024];
            chunk[0] = (byte)'%';
            chunk[1] = (byte)'P';
            chunk[2] = (byte)'D';
            chunk[3] = (byte)'F';
            chunk[4] = (byte)'-';
            var paused = false;
            var pauseAt = (5 * 1024 * 1024) + chunk.Length;
            while (written < total)
            {
                if (!paused && written >= pauseAt)
                {
                    paused = true;
                    await Task.Delay(500).ConfigureAwait(false);
                }

                var count = Math.Min(chunk.Length, total - written);
                await context.Response.OutputStream.WriteAsync(chunk.AsMemory(0, count)).ConfigureAwait(false);
                await context.Response.OutputStream.FlushAsync().ConfigureAwait(false);
                written += count;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            ChunkedAttachmentBytesWritten = written;
        }
    }

    private static async Task WriteAttachmentAsync(
        HttpListenerContext context,
        string contentType,
        string fileName,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = 200;
        context.Response.ContentType = contentType;
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{fileName}\"";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
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
