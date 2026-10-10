using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;
using SkiaSharp;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserHardeningTests
{
    [Fact]
    public async Task Screenshot_failures_are_structured_and_formats_recover()
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await NavigateHome(browser, id);
            var page = browser.ContextFor(id)!.Pages[0];
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Dialog += (_, _) => shown.TrySetResult();
            var alert = page.EvaluateAsync("() => alert('private')");
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var blocked = await browser.ExecuteAsync(new(id, new BrowserScreenshot()));
            Assert.Equal("dialog_pending", blocked.ErrorCode); Assert.Null(blocked.Bytes); Assert.Null(blocked.FileName);
            await browser.ExecuteAsync(new(id, new BrowserDialog("dismiss"))); await alert;
            await page.EvaluateAsync("() => document.body.style.height='13000px'");
            Assert.Equal("capture_too_large", (await browser.ExecuteAsync(new(id, new BrowserScreenshot(FullPage: true)))).ErrorCode);
            await page.GotoAsync("about:blank");
            Assert.Equal("target_denied", (await browser.ExecuteAsync(new(id, new BrowserScreenshot()))).ErrorCode);
            await NavigateHome(browser, id);
            foreach (var format in new[] { "png", "jpeg", "webp" })
            {
                var captured = await browser.ExecuteAsync(new(id, new BrowserScreenshot(format)));
                Assert.Null(captured.ErrorCode); Assert.NotEmpty(captured.Bytes!);
                Assert.Equal("image/" + format, captured.ContentType); Assert.Equal("screenshot." + format, captured.FileName);
                using var decoded = SKBitmap.Decode(captured.Bytes); Assert.True(decoded.Width > 0);
            }
            page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<button style='position:absolute;animation:move .1s infinite alternate'>Moving</button><style>@keyframes move{from{left:0}to{left:100px}}</style>");
            browser.OperationTimeout = TimeSpan.FromMilliseconds(250);
            Assert.Equal("timeout", (await browser.ExecuteAsync(new(id, new BrowserScreenshot(Target: new("role", "button", Name: "Moving"))))).ErrorCode);
            Assert.Equal(0, await page.Locator("[data-agent-mask]").CountAsync());
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Verification_requires_meaningful_text_and_keeps_intentional_empty_value()
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await NavigateHome(browser, id);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<h1>Record approved</h1><label>Notes<input value=''></label>");
            var target = new BrowserTarget("role", "heading", Name: "Record approved");
            foreach (var text in new string?[] { null, "", "   " })
            {
                var invalid = await browser.ExecuteAsync(new(id, new BrowserVerify("text", target, text)));
                Assert.Equal("invalid", invalid.ErrorCode); Assert.False(invalid.ApplicationOutcomeVerified);
            }
            Assert.True((await browser.ExecuteAsync(new(id, new BrowserVerify("text", target, "approved")))).ApplicationOutcomeVerified);
            Assert.False((await browser.ExecuteAsync(new(id, new BrowserVerify("text", target, "rejected")))).ApplicationOutcomeVerified);
            Assert.True((await browser.ExecuteAsync(new(id, new BrowserVerify("value", new("label", "Notes"), Value: "")))).ApplicationOutcomeVerified);
            Assert.False((await browser.ExecuteAsync(new(id, new BrowserClose()))).ApplicationOutcomeVerified);
        }
        finally { await browser.StopAsync(default); }
    }

    [Theory]
    [InlineData(false, "initScript")]
    [InlineData(false, "page")]
    [InlineData(false, "routes")]
    [InlineData(true, "persistent")]
    [InlineData(true, "initScript")]
    [InlineData(true, "page")]
    [InlineData(true, "routes")]
    public async Task Failed_initialization_closes_context_and_releases_profile(bool persistent, string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-hardening-" + Guid.NewGuid().ToString("N"));
        var browser = NewBrowser(persistent, root); await browser.StartAsync(default); var id = Guid.NewGuid();
        browser.BindSession(id, Guid.NewGuid()); IBrowserContext? failed = null;
        try
        {
            browser.InitializationProbe = (name, context) => { if (name == stage) { failed = context; throw new PlaywrightException("injected setup failure"); } return Task.CompletedTask; };
            Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins[0] + "/")))).ErrorCode);
            Assert.NotNull(failed); Assert.Empty(failed.Pages); Assert.Null(browser.ContextFor(id));
            browser.InitializationProbe = null; await NavigateHome(browser, id); Assert.NotNull(browser.ContextFor(id));
            await browser.ExecuteAsync(new(id, new BrowserClose()));
            await browser.ReleaseAsync(id); await browser.ReleaseAsync(id); Assert.False(browser.HasBinding(id));
        }
        finally { await browser.StopAsync(default); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, "initScript")]
    [InlineData(false, "page")]
    [InlineData(false, "routes")]
    [InlineData(true, "persistent")]
    public async Task Canceled_initialization_cleans_up_before_a_new_context_is_published(bool persistent, string stage)
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-cancel-init-" + Guid.NewGuid().ToString("N"));
        var browser = NewBrowser(persistent, root); await browser.StartAsync(default); var id = Guid.NewGuid(); browser.BindSession(id, Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IBrowserContext? abandoned = null;
        browser.InitializationProbe = async (name, context) => { if (name == stage) { abandoned = context; entered.TrySetResult(); await release.Task; } };
        try
        {
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins[0] + "/")), cancel.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.NotNull(abandoned); Assert.Empty(abandoned.Pages); Assert.Null(browser.ContextFor(id));
            release.TrySetResult(); browser.InitializationProbe = null; await NavigateHome(browser, id);
        }
        finally { release.TrySetResult(); await browser.StopAsync(default); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Strict_visual_confidentiality_disables_capture_without_losing_the_context()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0, ScreenshotPrivacy = "Disabled" }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await NavigateHome(browser, id);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(new(id, new BrowserScreenshot()))).ErrorCode);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task A_single_authenticated_request_supplies_each_document_and_attachment_and_cancellation_recovers()
    {
        await using var server = new CountingServer();
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false, NavigationOrigins = [server.Origin], InteractionOrigins = [server.Origin] }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(server.Origin + "/once")))).ErrorCode);
            Assert.Equal(1, server.Count("/once"));
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(server.Origin + "/login")))).ErrorCode);
            var attachment = await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(server.Origin + "/file")));
            Assert.Null(attachment.ErrorCode); Assert.Null(Assert.Single(attachment.Downloads!).ErrorCode);
            Assert.Equal(1, server.Count("/file")); Assert.True(server.Authenticated);
            await browser.ContextFor(id)!.Pages[0].SetContentAsync("<form method='post' action='/post-file'><input name='record' value='AC-1042'><button>Export</button></form>");
            var posted = await browser.ExecuteAsync(new(id, new BrowserClick(new("role", "button", Name: "Export"))));
            Assert.Null(posted.ErrorCode); Assert.Null(Assert.Single(posted.Downloads!).ErrorCode);
            Assert.Equal(1, server.Count("/post-file")); Assert.True(server.Authenticated); Assert.Equal("POST", server.LastMethod);
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(server.Origin + "/stalled")), cancel.Token).AsTask();
            await server.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            server.Release.TrySetResult();
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(server.Origin + "/recovery")))).ErrorCode);
            Assert.Equal(1, server.Count("/stalled"));
            Assert.DoesNotContain((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).Downloads ?? [], d => d.FileName == "late.csv");
        }
        finally { server.Release.TrySetResult(); await browser.StopAsync(default); }
    }

    [Fact]
    public async Task DNS_alias_to_link_local_is_denied_before_connection_in_both_modes()
    {
        foreach (var mode in new[] { BrowserPolicyMode.OpenWeb, BrowserPolicyMode.Restricted })
        {
            var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false, PolicyMode = mode.ToString(), NavigationOrigins = ["http://alias.invalid"] }, null);
            await browser.StartAsync(default);
            var resolutions = 0;
            browser.DestinationProxy!.ResolveAsync = (_, _) => { Interlocked.Increment(ref resolutions); return Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }); };
            try { Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Navigate(Guid.NewGuid(), new Uri("http://alias.invalid/")))).ErrorCode); Assert.True(resolutions > 0); }
            finally { await browser.StopAsync(default); }
        }
    }

    [Fact]
    public async Task DNS_pin_serves_an_alias_then_rejects_rebinding_and_redirects()
    {
        await using var server = new AliasServer();
        var port = new Uri(server.Origin).Port;
        var origin = $"http://pin.invalid:{port}";
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = [origin] }, null);
        await browser.StartAsync(default); var id = Guid.NewGuid();
        browser.DestinationProxy!.ResolveAsync = (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/once")))).ErrorCode);
            Assert.Equal(1, server.Count("/once"));
            Assert.Equal("target_denied", (await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/redirect-forbidden")))).ErrorCode);
            browser.DestinationProxy.ResolveAsync = (_, _) => Task.FromResult(new[] { IPAddress.Loopback, IPAddress.Parse("169.254.169.254") });
            Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/rebind")))).ErrorCode);
            Assert.Equal(0, server.Count("/rebind"));
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Empty_lease_denies_all_and_attached_calls_wait_for_disposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-lease-" + Guid.NewGuid().ToString("N"));
        var browser = NewBrowser(true, root); await browser.StartAsync(default);
        var agent = Guid.NewGuid(); var attached = Guid.NewGuid(); var detached = Guid.NewGuid();
        browser.BindSession(attached, agent); browser.BindSession(detached, agent);
        try
        {
            await NavigateHome(browser, attached);
            var context = browser.ContextFor(attached);
            var lease = await browser.EnterUnattendedAsync(agent, []);
            // Separate async flow keeps the attached caller outside the adopted occurrence.
            browser.ExpectInteractive(agent);
            var pending = browser.ExecuteAsync(BrowserTestRequests.Navigate(attached, new Uri(browser.HostPolicy.NavigationOrigins[0] + "/"))).AsTask();
            await browser.InteractiveEntered(agent); Assert.False(pending.IsCompleted);
            browser.AdoptUnattendedFlow(agent);
            Assert.Equal("target_denied", (await browser.ExecuteAsync(BrowserTestRequests.Navigate(detached, new Uri(browser.HostPolicy.NavigationOrigins[0] + "/")))).ErrorCode);
            await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.Null((await pending).ErrorCode);
            await using (var inherit = await browser.EnterUnattendedAsync(agent, null))
            {
                browser.AdoptUnattendedFlow(agent); await NavigateHome(browser, detached);
                Assert.Same(context, browser.ContextFor(detached));
            }
            await browser.ReleaseAsync(detached); Assert.Same(context, browser.ContextFor(attached));
        }
        finally { await browser.StopAsync(default); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("OpenWeb")]
    [InlineData("Restricted")]
    public async Task A_nonempty_occurrence_lease_only_narrows_host_authority(string mode)
    {
        await using var allowed = new CountingServer(); await using var excluded = new CountingServer();
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixtureEnabled = false,
            PolicyMode = mode, NavigationOrigins = [allowed.Origin] }, null);
        await browser.StartAsync(default); var agent = Guid.NewGuid(); var id = Guid.NewGuid(); browser.BindSession(id, agent);
        try
        {
            await using var lease = await browser.EnterUnattendedAsync(agent, [allowed.Origin, excluded.Origin]);
            browser.AdoptUnattendedFlow(agent);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(allowed.Origin + "/once")))).ErrorCode);
            var excludedResult = await browser.ExecuteAsync(new(id, new BrowserNavigate(excluded.Origin + "/once")));
            if (mode == "Restricted") { Assert.Equal("target_denied", excludedResult.ErrorCode); Assert.Equal(0, excluded.Count("/once")); }
            else { Assert.Null(excludedResult.ErrorCode); Assert.Equal(1, excluded.Count("/once")); }
            // Replace with a narrower occurrence: OpenWeb cannot ignore explicit origins.
            await lease.DisposeAsync();
            await using var narrowed = await browser.EnterUnattendedAsync(agent, [allowed.Origin]); browser.AdoptUnattendedFlow(agent);
            Assert.Equal("target_denied", (await browser.ExecuteAsync(new(id, new BrowserNavigate(excluded.Origin + "/blocked")))).ErrorCode);
            Assert.Equal(0, excluded.Count("/blocked"));
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Effective_limits_bound_native_snapshots_matches_input_and_capture_bytes()
    {
        var options = new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0,
            Limits = new(SnapshotBytes: 256, CaptureBytes: 1024, FindMatches: 1, TextInputLength: 5, AutomaticSettleMs: 100) };
        var browser = new NativePlaywrightBrowser(options, null); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await NavigateHome(browser, id); var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<label>Notes<input></label><button>Item</button><button>Item</button><p>" + new string('x', 1000) + "</p>");
            var observed = await browser.ExecuteAsync(new(id, new BrowserObserve()));
            Assert.Null(observed.ErrorCode); Assert.True(observed.Observation!.ContentTruncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(observed.Observation.Content!), 1, 256);
            var found = await browser.ExecuteAsync(new(id, new BrowserFind(new("role", "button"), Limit: 20)));
            using var json = JsonDocument.Parse(found.DataJson!); Assert.Single(json.RootElement.GetProperty("matches").EnumerateArray());
            Assert.Equal("invalid", (await browser.ExecuteAsync(new(id, new BrowserTypeText(new("label", "Notes"), "123456")))).ErrorCode);
            Assert.Equal("capture_too_large", (await browser.ExecuteAsync(new(id, new BrowserScreenshot()))).ErrorCode);
            options.Limits = options.Limits with { SnapshotBytes = 8000 };
            Assert.Equal(256, browser.HostPolicy.Limits.SnapshotBytes);
        }
        finally { await browser.StopAsync(default); }
    }

    [Fact]
    public async Task Screenshots_mask_canvas_svg_generated_text_and_remove_overlays()
    {
        var browser = NewBrowser(); await browser.StartAsync(default); var id = Guid.NewGuid();
        try
        {
            await NavigateHome(browser, id); var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<style>body{margin:0}.secret::before{content:'known-secret'}</style><canvas width='200' height='50'></canvas><svg width='200' height='50'><text y='30'>secret</text></svg><div class='secret'>safe</div>");
            await page.EvaluateAsync("() => {localStorage.setItem('token','known-secret');const c=document.querySelector('canvas').getContext('2d');c.fillStyle='red';c.fillRect(0,0,200,50);c.fillText('secret',10,20)}");
            var result = await browser.ExecuteAsync(new(id, new BrowserScreenshot())); Assert.Null(result.ErrorCode);
            using var bitmap = SKBitmap.Decode(result.Bytes); var pixel = bitmap.GetPixel(10, 10);
            Assert.True(pixel.Red < 25 && pixel.Green < 25 && pixel.Blue < 25);
            Assert.True(result.RedactionCount >= 3); Assert.Equal(0, await page.Locator("[data-agent-mask]").CountAsync());
        }
        finally { await browser.StopAsync(default); }
    }

    private static NativePlaywrightBrowser NewBrowser(bool persistent = false, string? root = null) => new(new() { Enabled = true, Headless = true, FixturePort = 0,
        ProfileMode = persistent ? "PersistentAgent" : "EphemeralSession", ProfileRoot = root ?? "data/browser-profiles" }, null);
    private static async Task NavigateHome(NativePlaywrightBrowser browser, Guid id) =>
        Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins[0] + "/")))).ErrorCode);

    // A loopback TCP listener accepts the original alias Host header without DNS
    // resolution by HttpListener's platform-specific prefix registration.
    private sealed class AliasServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ConcurrentDictionary<string, int> _counts = new();
        private readonly Task _loop;
        public string Origin { get; }
        public int Count(string path) => _counts.GetValueOrDefault(path);
        public AliasServer()
        {
            _listener.Start(); Origin = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Serve();
        }
        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    var first = await reader.ReadLineAsync();
                    if (first is null) continue;
                    while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                    var path = first.Split(' ')[1]; _counts.AddOrUpdate(path, 1, (_, n) => n + 1);
                    var response = path == "/redirect-forbidden"
                        ? "HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/never-contact\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: 14\r\nConnection: close\r\n\r\n<h1>Ready</h1>";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync() { _listener.Stop(); await _loop; }
    }

    private sealed class CountingServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly ConcurrentDictionary<string, int> _counts = new();
        private readonly Task _loop;
        public string Origin { get; }
        public bool Authenticated { get; private set; }
        public string? LastMethod { get; private set; }
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count(string path) => _counts.GetValueOrDefault(path);
        public CountingServer()
        {
            var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
            Origin = "http://127.0.0.1:" + port; _listener.Prefixes.Add(Origin + "/");
            _listener.Start(); _loop = Serve();
        }
        private async Task Serve()
        {
            try { while (_listener.IsListening) { var context = await _listener.GetContextAsync(); _ = Respond(context); } }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
        }
        private async Task Respond(HttpListenerContext context)
        {
            var path = context.Request.Url!.AbsolutePath; var count = _counts.AddOrUpdate(path, 1, (_, n) => n + 1);
            try
            {
                if (path == "/redirect-forbidden") { context.Response.Redirect("http://169.254.169.254/never-contact"); return; }
                if (path == "/login") context.Response.Headers.Add("Set-Cookie", "auth=yes; HttpOnly; Path=/");
                if (path is "/file" or "/post-file" or "/stalled")
                {
                    Authenticated = context.Request.Cookies["auth"]?.Value == "yes"; LastMethod = context.Request.HttpMethod;
                    context.Response.Headers.Add("Content-Disposition", "attachment; filename=" + (path == "/stalled" ? "late.csv" : "record.csv"));
                    context.Response.ContentType = "text/csv";
                    if (path == "/stalled")
                    {
                        context.Response.SendChunked = true;
                        await context.Response.OutputStream.WriteAsync("id,"u8.ToArray()); await context.Response.OutputStream.FlushAsync(); Stalled.TrySetResult(); await Release.Task;
                    }
                    await context.Response.OutputStream.WriteAsync("id,value\n1,approved\n"u8.ToArray());
                }
                else
                {
                    context.Response.StatusCode = path == "/once" && count > 1 ? 409 : 200;
                    context.Response.ContentType = "text/html";
                    await context.Response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<h1>Ready</h1>"));
                }
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException) { }
            finally { context.Response.Close(); }
        }
        public async ValueTask DisposeAsync() { Release.TrySetResult(); _listener.Close(); await _loop; }
    }
}
