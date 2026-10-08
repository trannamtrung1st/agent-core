using System.Text.Json;
using System.Text;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Tools;
using AgentCore.Infrastructure.Browser;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

public sealed class BrowserEnvironmentJourneyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_configuration_inspection_reports_disabled_or_missing_engine_without_opening_context(bool enabled)
    {
        var probes = 0;
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = enabled, FixtureEnabled = false,
            ProfileRoot = "private-profile-must-not-be-exposed" }, null, _ => { probes++; return Task.FromResult(false); });
        await browser.StartAsync(CancellationToken.None);
        var gate = new ToolConfigurationGate(null, null, null, browser, enabled);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: gate);
        var definition = new AgentDefinition(1, "inspection", 1, new("Inspection", "Role", "Description", "Tone"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048),
            new(false, 60_000, 120_000, 1, ["longSilence"], 0), new(false, "default", 1),
            new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new RoleEnvironment(ToolAllowlist: [ToolCatalog.BrowserConfiguration, ToolCatalog.BrowserNavigate]));
        var id = Guid.NewGuid();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        var call = new ModelToolCall("config", ToolCatalog.BrowserConfiguration, "{}");
        try
        {
            Assert.True(gate.IsConfigured(call.Name));
            Assert.True(gate.IsExecutionConfigured(call.Name));
            Assert.False(gate.IsConfigured(ToolCatalog.BrowserNavigate));
            Assert.False(gate.IsExecutionConfigured(ToolCatalog.BrowserNavigate));
            var result = await executor.ExecuteAsync(definition, id, call, ToolLimits.MaxOutputBytes, admission: admission);
            using var config = JsonDocument.Parse(result.Text);
            Assert.False(config.RootElement.GetProperty("available").GetBoolean());
            Assert.False(config.RootElement.GetProperty("contextOpen").GetBoolean());
            Assert.Equal("chromium", config.RootElement.GetProperty("engine").GetString());
            Assert.DoesNotContain("private-profile", result.Text);
            Assert.Null(browser.ContextFor(id));
            Assert.Equal(enabled ? 1 : 0, probes);
            Assert.Contains("forbidden", (await executor.ExecuteAsync(definition with { Environment = new RoleEnvironment(ToolAllowlist: []) },
                id, call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Context_creation_environment_and_runtime_media_are_observed_without_replacing_owner_state(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-environment-" + Guid.NewGuid().ToString("N"));
        var browser = new PlaywrightBrowser(new BrowserOptions
        {
            Enabled = true, Headless = true, FixturePort = 0, ProfileRoot = root,
            ProfileMode = persistent ? "PersistentAgent" : "EphemeralSession",
            Environment = new() { Device = "Pixel 5", Locale = "fr-FR", TimezoneId = "Europe/Paris" }
        }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        browser.BindSession(id, Guid.NewGuid());
        async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
        try
        {
            var closed = await Run(ToolCatalog.BrowserConfiguration, new { });
            Assert.Null(closed.ErrorCode);
            Assert.Contains("\"contextOpen\":false", closed.DataJson);
            Assert.Null(browser.ContextFor(id));
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(origin + "/browser-v2.html?compact=1")))).ErrorCode);
            var context = browser.ContextFor(id)!;
            var page = context.Pages[0];
            Assert.Equal("fr-FR", await page.EvaluateAsync<string>("navigator.language"));
            Assert.Equal("Europe/Paris", await page.EvaluateAsync<string>("Intl.DateTimeFormat().resolvedOptions().timeZone"));
            Assert.Equal(2.75, await page.EvaluateAsync<double>("devicePixelRatio"));
            Assert.True(await page.EvaluateAsync<bool>("navigator.maxTouchPoints > 0"));
            Assert.Contains("Mobile", await page.EvaluateAsync<string>("navigator.userAgent"));
            Assert.Equal(393, page.ViewportSize!.Width);
            Assert.Equal(393, await page.EvaluateAsync<int>("screen.width"));
            await page.EvaluateAsync("localStorage.setItem('draft', 'preserved')");
            Assert.Null((await Run(ToolCatalog.BrowserMedia, new { colorScheme = "dark", media = "print" })).ErrorCode);
            Assert.Null((await Run(ToolCatalog.BrowserMedia, new { reducedMotion = "reduce", forcedColors = "active", contrast = "more" })).ErrorCode);
            Assert.True(await page.EvaluateAsync<bool>("matchMedia('(prefers-color-scheme: dark)').matches && matchMedia('print').matches && matchMedia('(prefers-reduced-motion: reduce)').matches && matchMedia('(forced-colors: active)').matches && matchMedia('(prefers-contrast: more)').matches"));
            Assert.Null((await Run(ToolCatalog.BrowserMedia, new { })).ErrorCode);
            Assert.True(await page.EvaluateAsync<bool>("matchMedia('(prefers-color-scheme: dark)').matches"));
            Assert.Null((await Run(ToolCatalog.BrowserMedia, new { colorScheme = (string?)null, media = (string?)null })).ErrorCode);
            Assert.False(await page.EvaluateAsync<bool>("matchMedia('print').matches"));
            Assert.True(await page.EvaluateAsync<bool>("matchMedia('(prefers-reduced-motion: reduce)').matches"));
            Assert.Null((await Run(ToolCatalog.BrowserResize, new { width = 800, height = 600 })).ErrorCode);
            using var config = JsonDocument.Parse((await Run(ToolCatalog.BrowserConfiguration, new { })).DataJson!);
            Assert.Equal("chromium", config.RootElement.GetProperty("engine").GetString());
            var environment = config.RootElement.GetProperty("environment");
            Assert.Equal("fr-FR", environment.GetProperty("locale").GetString());
            Assert.Equal(800, environment.GetProperty("viewport").GetProperty("width").GetInt32());
            Assert.Equal("reduce", environment.GetProperty("mediaOverrides").GetProperty("reducedMotion").GetString());
            Assert.Equal(JsonValueKind.Null, environment.GetProperty("mediaOverrides").GetProperty("colorScheme").ValueKind);
            Assert.DoesNotContain(root, config.RootElement.GetRawText());
            Assert.DoesNotContain("fixture-protected-token", config.RootElement.GetRawText());
            Assert.Null((await Run("browser.network_state", new { online = false })).ErrorCode);
            Assert.False(await page.EvaluateAsync<bool>("navigator.onLine"));
            Assert.Contains("\"online\":false", (await Run(ToolCatalog.BrowserConfiguration, new { })).DataJson);
            Assert.Null((await Run("browser.network_state", new { online = true })).ErrorCode);
            Assert.True(await page.EvaluateAsync<bool>("navigator.onLine"));
            Assert.Same(context, browser.ContextFor(id));
            Assert.Equal("preserved", await page.EvaluateAsync<string>("localStorage.getItem('draft')"));
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true }).EvaluateAsync("el => { window.keys=[]; el.addEventListener('keydown', event => { if (event.key.length === 1) window.keys.push(event.key); }); }");
            var snapshot = await browser.SnapshotAsync(id);
            var reference = snapshot.Observation!.Elements.Single(e => e.Name == "Title").Ref;
            Assert.Null((await Run(ToolCatalog.BrowserType, new { @ref = reference, text = "abc", slowly = true })).ErrorCode);
            Assert.Equal(new[] { "a", "b", "c" }, await page.EvaluateAsync<string[]>("window.keys"));
            Assert.Equal("abc", await page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true }).InputValueAsync());
            reference = (await browser.SnapshotAsync(id)).Observation!.Elements.Single(e => e.Name == "Title").Ref;
            Assert.Null((await Run(ToolCatalog.BrowserHighlight, new { @ref = reference })).ErrorCode);
            Assert.Null((await Run(ToolCatalog.BrowserHighlight, new { operation = "hide" })).ErrorCode);
            Assert.Equal(0, await page.Locator("x-pw-tooltip").CountAsync());
            var worker = await page.EvaluateAsync<bool>("async () => { try { return !!(await navigator.serviceWorker.register('/browser-v2-worker.js')); } catch { return false; } }");
            Assert.False(worker);
            Assert.Equal(0, await page.EvaluateAsync<int>("async () => (await navigator.serviceWorker.getRegistrations()).length"));
        }
        finally
        {
            await browser.StopAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Wheel_uses_signed_deltas_at_a_safe_target_and_rejects_missing_or_unbounded_distances()
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        async Task<BrowserCommandResult> Run(object args) => await browser.ExecuteAsync(new(id, "browser.mouse", JsonSerializer.SerializeToElement(args)));
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            // A generic scrollable surface needs no application-specific selectors or fixture behavior.
            await page.EvaluateAsync("() => { const panel=document.createElement('div'); panel.id='wheel-probe'; panel.style='position:fixed;inset:100px auto auto 100px;width:300px;height:300px;overflow:scroll;z-index:999;background:white'; panel.innerHTML='<div style=\"width:2000px;height:2000px\">Scroll surface</div>'; document.body.append(panel); }");
            Assert.Equal("invalid", (await Run(new { operation = "wheel", x = 200, y = 200 })).ErrorCode);
            Assert.Equal("invalid", (await Run(new { operation = "wheel", x = 200, y = 200, deltaY = 2001 })).ErrorCode);
            Assert.Null((await Run(new { operation = "wheel", x = 200, y = 200, deltaX = 300, deltaY = 250 })).ErrorCode);
            await page.WaitForFunctionAsync("() => { const e=document.getElementById('wheel-probe'); return e.scrollLeft >= 300 && e.scrollTop >= 250; }");
            Assert.Null((await Run(new { operation = "wheel", x = 200, y = 200, deltaX = -300, deltaY = -250 })).ErrorCode);
            await page.WaitForFunctionAsync("() => { const e=document.getElementById('wheel-probe'); return e.scrollLeft === 0 && e.scrollTop === 0; }");
            Assert.Equal(new[] { 0, 0 }, await page.EvaluateAsync<int[]>("() => { const e=document.getElementById('wheel-probe'); return [e.scrollLeft,e.scrollTop]; }"));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Geolocation_requires_exact_active_origin_and_revokes_permission_without_echoing_coordinates()
    {
        await using var main = new LoopbackBrowserFixtureHost(NullLogger.Instance);
        await main.StartAsync(0, CancellationToken.None);
        await using var other = new LoopbackBrowserFixtureHost(NullLogger.Instance);
        await other.StartAsync(0, CancellationToken.None);
        var browser = new PlaywrightBrowser(new BrowserOptions
        {
            Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = [main.Origin!, other.Origin!], InteractionOrigins = [main.Origin!, other.Origin!]
        }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        async Task<BrowserCommandResult> Run(object args) => await browser.ExecuteAsync(new(id, ToolCatalog.BrowserGeolocation, JsonSerializer.SerializeToElement(args)));
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins[0];
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(origin + "/browser-v2.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            Assert.Equal("target_denied", (await Run(new { operation = "set", origin = other.Origin, latitude = 10, longitude = 20 })).ErrorCode);
            Assert.Equal("invalid", (await Run(new { operation = "set", origin = origin + "/path", latitude = 10, longitude = 20 })).ErrorCode);
            Assert.Equal("invalid", (await Run(new { operation = "set", origin, latitude = 91, longitude = 20 })).ErrorCode);
            Assert.Equal("invalid", (await Run(new { operation = "set", origin })).ErrorCode);
            await browser.ContextFor(id)!.GrantPermissionsAsync(["notifications"], new() { Origin = origin });
            var granted = await Run(new { operation = "set", origin, latitude = 10.75, longitude = 20.5, accuracy = 10 });
            Assert.Null(granted.ErrorCode);
            Assert.Equal("granted", await page.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'notifications'})).state"));
            Assert.Null((await Run(new { operation = "set", origin, latitude = 10.75, longitude = 20.5 })).ErrorCode);
            Assert.Equal("granted", await page.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'notifications'})).state"));
            Assert.DoesNotContain("10.75", granted.DataJson);
            var position = await page.EvaluateAsync<double[]>("() => new Promise((resolve,reject) => navigator.geolocation.getCurrentPosition(p => resolve([p.coords.latitude, p.coords.longitude]), reject))");
            Assert.Equal(new[] { 10.75, 20.5 }, position);
            var otherPage = await browser.ContextFor(id)!.NewPageAsync();
            await otherPage.GotoAsync(other.Origin + "/browser-v2-frame.html");
            Assert.Equal("prompt", await otherPage.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'geolocation'})).state"));
            var cleared = await Run(new { operation = "clear", origin });
            Assert.Null(cleared.ErrorCode);
            Assert.Contains("\"permissionsReset\":true", cleared.DataJson);
            Assert.Equal("prompt", await page.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'notifications'})).state"));
            Assert.Equal("prompt", await page.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'geolocation'})).state"));
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await browser.ExecuteAsync(new(id, ToolCatalog.BrowserGeolocation,
                JsonSerializer.SerializeToElement(new { operation = "set", origin, latitude = 10, longitude = 20 })), cancel.Token));
            Assert.Equal("prompt", await page.EvaluateAsync<string>("async () => (await navigator.permissions.query({name:'geolocation'})).state"));
            Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Websocket_resource_origins_allow_normal_spa_messages_and_block_unlisted_destinations()
    {
        var connections = new Dictionary<string, int> { ["allowed"] = 0, ["denied"] = 0 };
        async Task<WebApplication> Server(string label)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.Urls.Add("http://127.0.0.1:0");
            app.UseWebSockets();
            app.Run(async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 404; return; }
                lock (connections) connections[label]++;
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await socket.SendAsync(Encoding.UTF8.GetBytes(label), WebSocketMessageType.Text, true, CancellationToken.None);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await socket.ReceiveAsync(new byte[256], deadline.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            });
            await app.StartAsync();
            return app;
        }
        await using var allowed = await Server("allowed");
        await using var denied = await Server("denied");
        string Origin(WebApplication app) => app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await using var main = new LoopbackBrowserFixtureHost(NullLogger.Instance);
        await main.StartAsync(0, CancellationToken.None);
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = [main.Origin!], ResourceOrigins = [Origin(allowed)] }, loggerFactory: null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(main.Origin + "/browser-v2.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            var messages = new List<string>();
            page.Console += (_, message) => messages.Add(message.Text);
            // Chromium 153 separately gates loopback access. Grant it only in this isolated
            // fixture so this test exercises Core's resource policy rather than Chromium's prompt.
            await browser.ContextFor(id)!.GrantPermissionsAsync(["local-network-access"], new() { Origin = main.Origin });
            Task<string> Connect(string origin) => page.EvaluateAsync<string>("url => new Promise(resolve => { const socket = new WebSocket(url.replace('http:', 'ws:') + '/ws'); socket.onmessage = e => { resolve(e.data); socket.close(); }; socket.onclose = () => resolve('closed'); socket.onerror = () => resolve('error'); })", origin);
            var received = await Connect(Origin(allowed));
            Assert.True(received == "allowed", $"received={received}; server connections={connections["allowed"]}; console={string.Join(";", messages)}; resource origins={string.Join(',', browser.HostPolicy.EffectiveResourceOrigins)}");
            Assert.NotEqual("denied", await Connect(Origin(denied)));
            lock (connections) { Assert.Equal(1, connections["allowed"]); Assert.Equal(0, connections["denied"]); }
            Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

}
