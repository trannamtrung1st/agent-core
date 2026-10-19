using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    private static async Task<Microsoft.Playwright.IPage> RetirementPage(NativePlaywrightBrowser browser, TenantBrowserOwner owner, bool persistent)
    {
        Assert.Null(await browser.BindTenantIsolationAsync(owner, persistent ? BrowserProfileMode.PersistentAgent : BrowserProfileMode.EphemeralSession));
        var url = browser.Fixture.Origin + "/browser-native.html?owned=" + owner.SessionId;
        Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(url))).ErrorCode);
        return browser.ContextFor(owner.SessionId)!.Pages.Single(p => p.Url == url);
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_retirement_closes_only_its_owned_view_and_retains_sealed_ownership_and_persistent_bytes(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-retirement-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var a = Owner(); var b = a with { SessionId = Guid.NewGuid() }; var foreign = Owner();
            var first = await RetirementPage(browser, a, persistent); var sibling = await RetirementPage(browser, b, persistent); var other = await RetirementPage(browser, foreign, persistent);
            await first.EvaluateAsync("() => localStorage.setItem('retained','private profile state')");
            var old = await browser.PrepareTenantCommandAsync(a, new BrowserObserve()); Assert.Null(old.ErrorCode);
            Assert.Equal("closed", await browser.RetireTenantAsync(a, _ => ValueTask.FromResult(true)));
            Assert.True(first.IsClosed); Assert.False(sibling.IsClosed); Assert.False(other.IsClosed);
            Assert.Null(browser.ContextFor(a.SessionId)); Assert.Equal(0, (await browser.ObserveTenantSessionAsync(a)).LiveContexts);
            Assert.NotNull((await browser.ExecuteAsync(new(a.SessionId, new BrowserObserve()) { TenantOwner = a, TenantWitness = old.Witness })).ErrorCode);
            Assert.Equal("forbidden", (await browser.ExecuteAsync(new(a.SessionId, new BrowserObserve()))).ErrorCode);
            Assert.Equal("closed", await browser.RetireTenantAsync(b, _ => ValueTask.FromResult(true))); Assert.True(sibling.IsClosed); Assert.False(other.IsClosed);
            Assert.Equal("already_closed", await browser.RetireTenantAsync(a, _ => ValueTask.FromResult(true)));
            var reopened = await RetirementPage(browser, a, persistent);
            Assert.Equal(persistent ? "private profile state" : null, await reopened.EvaluateAsync<string?>("() => localStorage.getItem('retained')"));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_retirement_rechecks_lifecycle_inside_native_gates_and_never_accepts_foreign_ownership()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-retirement-race-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); var page = await RetirementPage(browser, owner, true); var checks = 0;
            Assert.Equal("active", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(++checks == 1))); Assert.Equal(2, checks); Assert.False(page.IsClosed);
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.RetireTenantAsync(owner with { TenantId = Guid.NewGuid() }, _ => throw new InvalidOperationException()).AsTask());
            var candidates = await browser.ListTenantRetirementCandidatesAsync(null, 1); Assert.Equal(owner, Assert.Single(candidates.Items));
            Assert.Null(candidates.NextCursor); Assert.False(page.IsClosed);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_retirement_busy_work_stays_live_and_a_restored_lifecycle_clears_only_unstarted_cleanup(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-retirement-busy-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var owner = Owner(); var page = await RetirementPage(browser, owner, persistent); var command = new BrowserObserve(); var prepared = await browser.PrepareTenantCommandAsync(owner, command);
            browser.TenantTargetValidatedProbe = async () => { entered.TrySetResult(); await release.Task; };
            var running = browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness }).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("cleanup_pending", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(true))); Assert.False(page.IsClosed);
            Assert.Equal("cleanup_pending", (await browser.ObserveTenantSessionAsync(owner)).Code);
            Assert.Equal("active", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(false)));
            release.TrySetResult(); Assert.Null((await running).ErrorCode); browser.TenantTargetValidatedProbe = null;
            Assert.False(page.IsClosed); Assert.Null((await browser.ObserveTenantSessionAsync(owner)).Code);
            Assert.Equal("closed", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(true))); Assert.True(page.IsClosed);
        }
        finally { release.TrySetResult(); browser.TenantTargetValidatedProbe = null; await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_retirement_unconfirmed_close_blocks_reuse_and_finishes_only_the_original_context_after_restore(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-retirement-close-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); var page = await RetirementPage(browser, owner, persistent);
            browser.ExplicitCloseProbe = _ => Task.CompletedTask;
            Assert.Equal("cleanup_pending", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(true))); Assert.False(page.IsClosed);
            Assert.Equal("policy_transition_pending", (await browser.PrepareTenantCommandAsync(owner, new BrowserObserve())).ErrorCode);
            browser.ExplicitCloseProbe = null;
            Assert.Equal("closed", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(false))); Assert.True(page.IsClosed);
            var fresh = await RetirementPage(browser, owner, persistent);
            Assert.Equal("active", await browser.RetireTenantAsync(owner, _ => ValueTask.FromResult(false))); Assert.False(fresh.IsClosed);
        }
        finally { browser.ExplicitCloseProbe = null; await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
