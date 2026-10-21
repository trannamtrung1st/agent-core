using AgentCore.Application.Ports;
using AgentCore.Application.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    [Fact]
    public async Task Tenant_request_retirement_cold_probe_never_binds_or_claims_a_namespace()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-request-cold-" + Guid.NewGuid().ToString("N")); var browser = Native(root); var owner = Owner();
        Assert.Equal("already_closed", await browser.RetireTenantIfBoundAsync(owner, _ => ValueTask.FromResult(false)));
        Assert.False(Directory.Exists(root)); browser.BindSession(owner.SessionId, owner.AgentInstanceId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.RetireTenantIfBoundAsync(owner, _ => ValueTask.FromResult(true)).AsTask());
        await browser.StopAsync(CancellationToken.None);
    }

    [TenantChromiumTheory] [InlineData(false)] [InlineData(true)]
    public async Task Tenant_request_retirement_rechecks_exact_proof_and_preserves_a_sibling_view(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-request-retirement-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var first = Owner(); var second = first with { SessionId = Guid.NewGuid() };
            var a = await RetirementPage(browser, first, persistent); var b = await RetirementPage(browser, second, persistent);
            Assert.Equal("active", await browser.RetireTenantIfBoundAsync(first, _ => ValueTask.FromResult(false))); Assert.False(a.IsClosed); Assert.False(b.IsClosed);
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.RetireTenantIfBoundAsync(first with { TenantId = Guid.NewGuid() }, _ => ValueTask.FromResult(true)).AsTask());
            Assert.Equal("closed", await browser.RetireTenantIfBoundAsync(first, _ => ValueTask.FromResult(true))); Assert.True(a.IsClosed); Assert.False(b.IsClosed);
            Assert.Equal("already_closed", await browser.RetireTenantIfBoundAsync(first, _ => ValueTask.FromResult(true)));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Tenant_runtime_browser_cold_binding_preserves_unknown_sharing_and_disabled_owner_fences()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-state-cold-" + Guid.NewGuid().ToString("N"));
        var options = new AgentCore.Infrastructure.Browser.BrowserOptions { Enabled = true, ProfileMode = "PersistentAgent", ProfileRoot = root };
        var browser = new AgentCore.Infrastructure.Browser.NativePlaywrightBrowser(options, null); var owner = Owner();
        browser.BindTenantSession(owner);
        var observation = await browser.ObserveTenantAgentAsync(owner.Agent, null);
        Assert.Equal("unknown", observation.Sharing); Assert.Equal("unavailable", observation.Availability); Assert.Null(observation.LiveContexts); Assert.False(Directory.Exists(root));
        options.Enabled = false;
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ObserveTenantAgentAsync(owner.Agent with { TenantId = Guid.NewGuid() }, null).AsTask());
        await browser.StopAsync(CancellationToken.None);
    }

    [TenantChromiumTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Tenant_runtime_browser_observation_deduplicates_shared_contexts_and_filters_exact_sessions(bool persistent)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-state-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var a = Owner(); var b = a with { SessionId = Guid.NewGuid() }; var foreign = Owner();
            await RetirementPage(browser, a, persistent); var sibling = await RetirementPage(browser, b, persistent); await RetirementPage(browser, foreign, persistent);
            var all = await browser.ObserveTenantAgentAsync(a.Agent, null); Assert.Equal(persistent ? 1 : 2, all.LiveContexts); Assert.Equal(2, all.OpenPages);
            var selected = await browser.ObserveTenantAgentAsync(a.Agent, [a.SessionId]); Assert.Equal(1, selected.LiveContexts); Assert.Equal(1, selected.OpenPages);
            var none = await browser.ObserveTenantAgentAsync(a.Agent, []); Assert.Equal(0, none.LiveContexts); Assert.Equal(0, none.OpenPages);
            await sibling.CloseAsync();
            Assert.Equal(1, (await browser.ObserveTenantAgentAsync(a.Agent, null)).OpenPages);
            Assert.Equal(0, (await browser.ObserveTenantAgentAsync(a.Agent, [b.SessionId])).OpenPages);
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ObserveTenantAgentAsync(a.Agent with { TenantId = foreign.TenantId }, null).AsTask());
            var cold = Owner(); var untouched = Path.Combine(root, cold.AgentInstanceId.ToString("D"));
            Assert.Equal(0, (await browser.ObserveTenantAgentAsync(cold.Agent, null)).LiveContexts);
            Assert.Null(browser.ContextFor(cold.SessionId)); Assert.False(Directory.Exists(untouched));
            browser.BindSession(cold.SessionId, cold.AgentInstanceId); // Observing did not claim its namespace.
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
