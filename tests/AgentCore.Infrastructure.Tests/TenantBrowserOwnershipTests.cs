using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed partial class TenantBrowserOwnershipTests
{
    private static TenantBrowserOwner Owner() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static BrowserRequest Inspect(TenantBrowserOwner owner) => new(owner.SessionId, new BrowserObserve()) { TenantOwner = owner };

    [Fact]
    public async Task Tenant_binding_is_immutable_and_unscoped_entry_points_cannot_adopt_read_reset_or_release_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-cold-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new() { Enabled = false, ProfileMode = "PersistentAgent", ProfileRoot = root }, null);
        var owner = Owner(); browser.BindTenantSession(owner); browser.BindTenantSession(owner);
        foreach (var other in new[] { owner with { TenantId = Guid.NewGuid() }, owner with { AgentInstanceId = Guid.NewGuid() }, owner with { ExecutorPrincipalId = Guid.NewGuid() } })
        {
            Assert.Throws<AccessDeniedException>(() => browser.BindTenantSession(other));
            Assert.Equal("forbidden", (await browser.ExecuteAsync(Inspect(other))).ErrorCode);
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ObserveTenantSessionAsync(other).AsTask());
            await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ReleaseTenantAsync(other).AsTask());
        }
        Assert.Throws<ArgumentException>(() => browser.BindTenantSession(owner with { TenantId = Guid.Empty }));
        Assert.Equal("forbidden", (await browser.ExecuteAsync(new(owner.SessionId, new BrowserObserve()))).ErrorCode);
        Assert.Equal("forbidden", (await browser.ExecuteAsync(Inspect(owner) with { SessionId = Guid.NewGuid() })).ErrorCode);
        Assert.Equal("provider_unavailable", (await browser.ExecuteAsync(Inspect(owner))).ErrorCode);
        Assert.Throws<AccessDeniedException>(() => browser.BindSession(owner.SessionId, owner.AgentInstanceId));
        Assert.Throws<AccessDeniedException>(() => browser.BindSession(Guid.NewGuid(), owner.AgentInstanceId));
        Assert.Equal("forbidden", await browser.BindIsolationAsync(owner.SessionId, owner.AgentInstanceId, BrowserProfileMode.PersistentAgent));
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ResetPersistentProfileAsync(owner.AgentInstanceId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ObserveAsync(owner.AgentInstanceId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.ReleaseAsync(owner.SessionId).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => browser.EnterUnattendedAsync(owner.AgentInstanceId, null).AsTask());
        Assert.Throws<AccessDeniedException>(() => browser.AdoptUnattendedFlow(owner.AgentInstanceId));
        var resolved = false;
        Assert.Equal("forbidden", (await browser.FillCredentialAsync(owner.SessionId, new("label", "Password"), (_, _) => { resolved = true; return ValueTask.FromResult("secret"); })).ErrorCode);
        Assert.False(resolved);
        var cold = await browser.ObserveTenantSessionAsync(owner);
        Assert.Equal("unavailable", cold.Availability); Assert.Equal("unknown", cold.Sharing); Assert.Null(cold.OpenPages);
        Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
        Assert.Equal("isolated", (await browser.ObserveTenantSessionAsync(owner)).Sharing);
        await browser.ReleaseTenantAsync(owner);
        Assert.Throws<AccessDeniedException>(() => browser.BindSession(owner.SessionId, owner.AgentInstanceId));
        browser.BindTenantSession(owner); // Same saved owner can resume after technical release.
        Assert.False(Directory.Exists(root));
        await browser.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unscoped_binding_or_command_reserves_its_namespace_and_cannot_be_promoted_to_tenant_ownership()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = false }, null); var owner = Owner();
        browser.BindSession(Guid.NewGuid(), owner.AgentInstanceId);
        Assert.Throws<AccessDeniedException>(() => browser.BindTenantSession(owner));
        var other = Owner();
        await browser.ExecuteAsync(new(other.SessionId, new BrowserGetConfig()));
        Assert.Throws<AccessDeniedException>(() => browser.BindTenantSession(other));
        var fresh = Owner();
        Assert.Equal("unavailable", (await browser.ObserveTenantSessionAsync(fresh)).Availability);
        browser.BindSession(fresh.SessionId, fresh.AgentInstanceId); // Observation did not bind anything.
        await browser.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Racing_tenant_binding_and_unscoped_command_cannot_both_claim_one_Session()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = false }, null); var owner = Owner();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bound = Task.Run(async () => {
            await start.Task;
            try { browser.BindTenantSession(owner); return true; } catch (AccessDeniedException) { return false; }
        });
        var command = Task.Run(async () => {
            await start.Task;
            return await browser.ExecuteAsync(new(owner.SessionId, new BrowserGetConfig()));
        });
        start.SetResult(); await Task.WhenAll(bound, command);
        Assert.Equal(await bound, (await command).ErrorCode == "forbidden");
        await browser.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancelled_unscoped_calls_do_not_reserve_a_tenant_namespace()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = false }, null);
        var cancelled = new CancellationToken(canceled: true);
        var released = Owner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ReleaseAsync(released.SessionId, cancelled).AsTask());
        browser.BindTenantSession(released);
        var filled = Owner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.FillCredentialAsync(filled.SessionId, new("label", "Password"), (_, _) => throw new InvalidOperationException("No secret read."), cancelled).AsTask());
        browser.BindTenantSession(filled);
        var unattended = Owner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.EnterUnattendedAsync(unattended.AgentInstanceId, null, cancelled).AsTask());
        browser.BindTenantSession(unattended);
        var isolation = Owner();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.BindIsolationAsync(isolation.SessionId, isolation.AgentInstanceId, BrowserProfileMode.EphemeralSession, cancelled).AsTask());
        browser.BindTenantSession(isolation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ObserveAsync(isolation.AgentInstanceId, cancelled).AsTask());
        await browser.StopAsync(CancellationToken.None);
    }
}
