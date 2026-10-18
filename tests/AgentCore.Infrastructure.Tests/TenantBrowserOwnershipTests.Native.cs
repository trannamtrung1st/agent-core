using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    private sealed class TenantChromiumFactAttribute : FactAttribute
    {
        public TenantChromiumFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AGENTCORE_CHROMIUM_ISOLATION_TESTS") != "1")
                Skip = "Opt in with AGENTCORE_CHROMIUM_ISOLATION_TESTS=1; uses disposable local Chromium profiles.";
        }
    }
    private static NativePlaywrightBrowser Native(string root, int port = 0) => new(new() { Enabled = true, Headless = true, FixturePort = port,
        ProfileRoot = root, ProfileMode = "PersistentAgent" }, null);
    private static async Task Open(NativePlaywrightBrowser browser, TenantBrowserOwner owner)
    {
        Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.PersistentAgent));
        var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html";
        Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(url))).ErrorCode);
    }
    private static Microsoft.Playwright.IPage Page(NativePlaywrightBrowser browser, TenantBrowserOwner owner) =>
        browser.ContextFor(owner.SessionId)!.Pages.Single(p => p.Url.EndsWith("/browser-native.html", StringComparison.Ordinal));

    [TenantChromiumFact]
    public async Task Tenant_native_profiles_isolate_legacy_and_other_tenant_storage_and_reopen_only_the_exact_saved_owner()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-native-" + Guid.NewGuid().ToString("N"));
        var a = Owner(); var b = Owner(); var legacySession = Guid.NewGuid(); var port = 0;
        try
        {
            var legacy = Native(root); await legacy.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(legacy.IsAvailable); port = legacy.Fixture.Port!.Value; legacy.BindSession(legacySession, a.AgentInstanceId);
                var url = legacy.HostPolicy.NavigationOrigins.Single() + "/browser-native.html";
                Assert.Null((await legacy.ExecuteAsync(new(legacySession, new BrowserNavigate(url)))).ErrorCode);
                await legacy.ContextFor(legacySession)!.Pages.Single(p => p.Url == url).EvaluateAsync("localStorage.setItem('owner','legacy')");
            }
            finally { await legacy.StopAsync(CancellationToken.None); }
            var browser = Native(root, port); await browser.StartAsync(CancellationToken.None);
            try
            {
                Assert.True(browser.IsAvailable); await Open(browser, a); await Open(browser, b);
                Assert.NotSame(browser.ContextFor(a.SessionId), browser.ContextFor(b.SessionId));
                Assert.Null(await Page(browser, a).EvaluateAsync<string?>("localStorage.getItem('owner')"));
                await Page(browser, a).EvaluateAsync("() => { localStorage.setItem('owner','tenant-A'); document.cookie='owner=tenant-A; path=/; max-age=3600'; }");
                Assert.DoesNotContain("owner=", await Page(browser, b).EvaluateAsync<string>("document.cookie"));
                Assert.Null(await Page(browser, b).EvaluateAsync<string?>("localStorage.getItem('owner')"));
                var sibling = a with { SessionId = Guid.NewGuid() }; await Open(browser, sibling);
                Assert.Same(browser.ContextFor(a.SessionId), browser.ContextFor(sibling.SessionId));
                var pages = browser.ContextFor(a.SessionId)!.Pages;
                foreach (var page in pages) Assert.Equal("tenant-A", await page.EvaluateAsync<string?>("localStorage.getItem('owner')"));
                var ownTab = Assert.Single((await Dispatch(browser, a, new BrowserTabs("list"))).Pages!);
                Assert.NotNull((await Dispatch(browser, b, new BrowserTabs("switch", ownTab.PageId))).ErrorCode);
                Assert.NotNull((await Dispatch(browser, sibling, new BrowserTabs("switch", ownTab.PageId))).ErrorCode);
                Assert.Throws<AccessDeniedException>(() => browser.BindTenantSession(a with { TenantId = b.TenantId, SessionId = Guid.NewGuid() }));
                var state = await browser.ObserveTenantSessionAsync(a); Assert.Equal(1, state.LiveContexts); Assert.Equal(1, state.OpenPages);
                await browser.ReleaseTenantAsync(a);
                Assert.Equal("tenant-A", await browser.ContextFor(sibling.SessionId)!.Pages.Single().EvaluateAsync<string?>("localStorage.getItem('owner')"));
                Assert.Equal("forbidden", (await browser.ExecuteAsync(new(sibling.SessionId, new BrowserObserve()))).ErrorCode);
                // Technical release keeps the trusted owner and selected isolation mode.
                var reopenedView = await Dispatch(browser, a, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html"));
                Assert.Null(reopenedView.ErrorCode); Assert.Same(browser.ContextFor(a.SessionId), browser.ContextFor(sibling.SessionId));
            }
            finally { await browser.StopAsync(CancellationToken.None); }
            var reopened = Native(root, port); await reopened.StartAsync(CancellationToken.None);
            try
            {
                var next = a with { SessionId = Guid.NewGuid() }; await Open(reopened, next);
                Assert.Equal("tenant-A", await Page(reopened, next).EvaluateAsync<string?>("localStorage.getItem('owner')"));
                Assert.Contains("owner=tenant-A", await Page(reopened, next).EvaluateAsync<string>("document.cookie"));
                var other = b with { SessionId = Guid.NewGuid() }; await Open(reopened, other);
                Assert.Null(await Page(reopened, other).EvaluateAsync<string?>("localStorage.getItem('owner')"));
                Assert.True(Directory.Exists(Path.Combine(root, "tenants", a.TenantId.ToString("D"), "agents", a.AgentInstanceId.ToString("D"))));
                Assert.True(Directory.Exists(Path.Combine(root, a.AgentInstanceId.ToString("D"))));
                Assert.Throws<AccessDeniedException>(() => reopened.BindTenantSession(next with { ExecutorPrincipalId = Guid.NewGuid() }));
            }
            finally { await reopened.StopAsync(CancellationToken.None); }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_profile_parent_symlink_is_refused_without_touching_the_destination()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-link-" + Guid.NewGuid().ToString("N")); var owner = Owner();
        var target = Path.Combine(root, "retained"); Directory.CreateDirectory(target); var marker = Path.Combine(target, "marker"); await File.WriteAllTextAsync(marker, "retained");
        Directory.CreateDirectory(Path.Combine(root, "tenants"));
        Directory.CreateSymbolicLink(Path.Combine(root, "tenants", owner.TenantId.ToString("D")), target);
        var browser = Native(root); await browser.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(browser.IsAvailable); Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.PersistentAgent));
            var result = await Dispatch(browser, owner, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html"));
            Assert.Equal("profile_unavailable", result.ErrorCode); Assert.Equal("retained", await File.ReadAllTextAsync(marker));
            Assert.Null(browser.ContextFor(owner.SessionId));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
