using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserStateIsolationTests
{
    private sealed class ChromiumIsolationFactAttribute : FactAttribute
    {
        public ChromiumIsolationFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("AGENTCORE_CHROMIUM_ISOLATION_TESTS") != "1")
                Skip = "Opt in with AGENTCORE_CHROMIUM_ISOLATION_TESTS=1; uses disposable local Chromium profiles.";
        }
    }

    [ChromiumIsolationFact]
    public Task Ephemeral_sessions_have_distinct_tabs_cookies_storage_and_independent_close() => TwoSessions(BrowserProfileMode.EphemeralSession);

    [ChromiumIsolationFact]
    public Task Persistent_sessions_share_auth_but_cannot_control_foreign_pages_or_close_siblings() => TwoSessions(BrowserProfileMode.PersistentAgent);

    private static async Task TwoSessions(BrowserProfileMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-state-isolation-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0,
            ProfileRoot = root, ProfileMode = "PersistentAgent" }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(browser.IsAvailable);
            var owner = Guid.NewGuid(); var a = Guid.NewGuid(); var b = Guid.NewGuid();
            Assert.Null(await browser.BindIsolationAsync(a, owner, mode));
            Assert.Null(await browser.BindIsolationAsync(b, owner, mode));
            Assert.Equal("forbidden", await browser.BindIsolationAsync(a, Guid.NewGuid(), mode));
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            var navigations = await Task.WhenAll(
                browser.ExecuteAsync(BrowserTestRequests.Navigate(a, new Uri(origin + "/browser-native.html?customer=A"))).AsTask(),
                browser.ExecuteAsync(BrowserTestRequests.Navigate(b, new Uri(origin + "/browser-native.html?customer=B"))).AsTask());
            Assert.All(navigations, r => Assert.Null(r.ErrorCode));
            var ca = browser.ContextFor(a)!; var cb = browser.ContextFor(b)!;
            Assert.Equal(mode == BrowserProfileMode.PersistentAgent, ReferenceEquals(ca, cb));
            var pa = ca.Pages.Single(p => p.Url.EndsWith("?customer=A", StringComparison.Ordinal));
            var pb = cb.Pages.Single(p => p.Url.EndsWith("?customer=B", StringComparison.Ordinal));
            await pa.EvaluateAsync("() => { document.cookie='customer=A; path=/'; localStorage.setItem('customer','A'); }");
            Assert.Equal(mode == BrowserProfileMode.PersistentAgent ? "A" : null, await pb.EvaluateAsync<string?>("localStorage.getItem('customer')"));
            Assert.Equal(mode == BrowserProfileMode.PersistentAgent, (await pb.EvaluateAsync<string>("document.cookie")).Contains("customer=A"));
            var ta = await browser.ExecuteAsync(BrowserTestRequests.Tabs(a, "list"));
            var tb = await browser.ExecuteAsync(BrowserTestRequests.Tabs(b, "list"));
            var ownedA = Assert.Single(ta.Pages!); var ownedB = Assert.Single(tb.Pages!);
            Assert.NotEqual(ownedA.PageId, ownedB.PageId);
            Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Tabs(b, "switch", ownedA.PageId))).ErrorCode);
            Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Tabs(b, "close", ownedA.PageId))).ErrorCode);
            Assert.False(pa.IsClosed);
            if (mode == BrowserProfileMode.PersistentAgent)
            {
                // B was the most recently active view. A's delayed popup still belongs to A.
                var popup = await pa.RunAndWaitForPopupAsync(() => pa.EvaluateAsync("window.open('/browser-native.html?popup=A')"));
                await popup.WaitForLoadStateAsync();
                var aPages = (await browser.ExecuteAsync(BrowserTestRequests.Tabs(a, "list"))).Pages!;
                Assert.Equal(2, aPages.Count);
                Assert.Single((await browser.ExecuteAsync(BrowserTestRequests.Tabs(b, "list"))).Pages!);
                var ownedPopup = aPages.Single(p => p.PageId != ownedA.PageId);
                Assert.NotNull((await browser.ExecuteAsync(BrowserTestRequests.Tabs(b, "switch", ownedPopup.PageId))).ErrorCode);
                Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Tabs(a, "close", ownedPopup.PageId))).ErrorCode);
            }
            var closed = await browser.ExecuteAsync(BrowserTestRequests.Close(b));
            Assert.Null(closed.ErrorCode); Assert.False(pa.IsClosed);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(a))).ErrorCode);
            Assert.Equal("A", await pa.EvaluateAsync<string>("localStorage.getItem('customer')"));
            var observation = await browser.ObserveAsync(owner);
            Assert.Equal(1, observation.LiveContexts); Assert.Equal(1, observation.OpenPages);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [ChromiumIsolationFact]
    public async Task Tightening_during_provisioning_is_pending_then_replaces_only_the_owned_view_preserving_profile_bytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-state-transition-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0,
            ProfileRoot = root, ProfileMode = "PersistentAgent" }, null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.InitializationProbe = async (stage, _) => { if (stage == "page") { entered.TrySetResult(); await release.Task; } };
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Guid.NewGuid(); var a = Guid.NewGuid();
            Assert.Null(await browser.BindIsolationAsync(a, owner, BrowserProfileMode.PersistentAgent));
            var navigation = browser.ExecuteAsync(BrowserTestRequests.Navigate(a, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html"))).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal("policy_transition_pending", await browser.BindIsolationAsync(a, owner, BrowserProfileMode.EphemeralSession));
            release.TrySetResult(); Assert.Null((await navigation).ErrorCode);
            var original = browser.ContextFor(a)!;
            await original.Pages.Single().EvaluateAsync("localStorage.setItem('customer','retained')");
            Assert.Null(await browser.BindIsolationAsync(a, owner, BrowserProfileMode.EphemeralSession));
            Assert.True(Directory.Exists(Path.Combine(root, owner.ToString("D"))));
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(a, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html")))).ErrorCode);
            var isolated = browser.ContextFor(a)!;
            Assert.NotSame(original, isolated);
            Assert.Null(await isolated.Pages.Single().EvaluateAsync<string?>("localStorage.getItem('customer')"));
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
