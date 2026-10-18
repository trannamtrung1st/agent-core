using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantBrowserOwnershipTests
{
    [TenantChromiumFact]
    public async Task Tenant_native_dispatch_check_and_disabled_host_prevent_closure_without_consuming_the_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-dispatch-gate-" + Guid.NewGuid().ToString("N"));
        var options = new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0, ProfileRoot = root, ProfileMode = "EphemeralSession" };
        var browser = new NativePlaywrightBrowser(options, null); await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(browser.Fixture.Origin + "/browser-native.html"))).ErrorCode);
            var command = new BrowserClose(); var prepared = await browser.PrepareTenantCommandAsync(owner, command);
            var checks = 0; var deny = true;
            var request = new BrowserRequest(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness,
                TenantDispatchCheck = _ => { checks++; return ValueTask.FromResult<string?>(deny ? "permission_denied" : null); } };
            Assert.DoesNotContain(nameof(BrowserRequest.TenantDispatchCheck), System.Text.Json.JsonSerializer.Serialize(request));
            options.Enabled = false;
            Assert.Equal("permission_denied", (await browser.ExecuteAsync(request)).ErrorCode); Assert.NotNull(browser.ContextFor(owner.SessionId));
            deny = false;
            Assert.Equal("provider_unavailable", (await browser.ExecuteAsync(request)).ErrorCode); Assert.NotNull(browser.ContextFor(owner.SessionId));
            Assert.Equal(2, checks); options.Enabled = true;
            Assert.Equal(prepared.Witness, (await browser.PrepareTenantCommandAsync(owner, command)).Witness);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_model_cold_binding_never_allocates_or_transitions_an_existing_native_view()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-cold-binding-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
            FixturePort = 0, ProfileRoot = root, ProfileMode = "PersistentAgent" }, null);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); Assert.Null(await browser.EnsureTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            Assert.Null(browser.ContextFor(owner.SessionId));
            Assert.Null((await Dispatch(browser, owner, new BrowserNavigate(browser.Fixture.Origin + "/browser-native.html"))).ErrorCode);
            var context = browser.ContextFor(owner.SessionId); var before = (await browser.PrepareTenantCommandAsync(owner, new BrowserClose())).Witness;
            Assert.Equal("policy_transition_pending", await browser.EnsureTenantIsolationAsync(owner, BrowserProfileMode.PersistentAgent));
            Assert.Same(context, browser.ContextFor(owner.SessionId)); Assert.Equal(before, (await browser.PrepareTenantCommandAsync(owner, new BrowserClose())).Witness);
            Assert.Null(await browser.EnsureTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            Assert.Equal(before, (await browser.PrepareTenantCommandAsync(owner, new BrowserClose())).Witness);
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task<BrowserResult> Dispatch(NativePlaywrightBrowser browser, TenantBrowserOwner owner, BrowserCommand command)
    {
        var prepared = await browser.PrepareTenantCommandAsync(owner, command);
        Assert.Null(prepared.ErrorCode); Assert.NotNull(prepared.Witness);
        return await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness });
    }

    [Fact]
    public void Browser_command_identity_binds_exact_typed_arguments_and_actual_upload_bytes()
    {
        var command = new BrowserNavigate("https://example.test/first");
        Assert.Equal(BrowserCommandIdentity.Hash(command), BrowserCommandIdentity.Hash(command with { }));
        Assert.NotEqual(BrowserCommandIdentity.Hash(command), BrowserCommandIdentity.Hash(command with { Url = "https://example.test/second" }));
        var bytes = new byte[] { 1, 2, 3 };
        var upload = new BrowserUploadCommand(new("label", "Upload"), ["artifact-a"], [new("data.bin", "application/octet-stream", bytes)]);
        var hash = BrowserCommandIdentity.Hash(upload); var frozen = BrowserCommandIdentity.Freeze(upload); bytes[0] = 4;
        Assert.NotEqual(hash, BrowserCommandIdentity.Hash(upload));
        Assert.Equal(hash, BrowserCommandIdentity.Hash(frozen));
        var fields = new[] { new BrowserFormField(new("label", "Name"), "Original") };
        var form = BrowserCommandIdentity.Freeze(new BrowserFillForm(fields)); var before = BrowserCommandIdentity.Hash(form);
        fields[0] = fields[0] with { Value = "Changed" };
        Assert.Equal(before, BrowserCommandIdentity.Hash(form));
        Assert.DoesNotContain("data.bin", hash); Assert.Equal(64, hash.Length);
    }

    [TenantChromiumFact]
    public async Task Tenant_command_target_preparation_is_non_provisioning_and_changed_or_replayed_commands_never_reach_native_work()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-target-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(browser.IsAvailable); var owner = Owner();
            Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var command = new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html");
            var prepared = await browser.PrepareTenantCommandAsync(owner, command); Assert.Null(prepared.ErrorCode);
            Assert.Null(browser.ContextFor(owner.SessionId)); Assert.False(Directory.Exists(root));
            Assert.Null(prepared.Witness!.ContextId); Assert.Null(prepared.Witness.TabRef);
            Assert.Equal("browser_target_required", (await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner })).ErrorCode);
            var drifted = new BrowserRequest(owner.SessionId, command with { Url = command.Url + "?drift=true" }) { TenantOwner = owner, TenantWitness = prepared.Witness };
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(drifted)).ErrorCode); Assert.Null(browser.ContextFor(owner.SessionId));
            var request = new BrowserRequest(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness };
            var competing = await Task.WhenAll(browser.ExecuteAsync(request).AsTask(), browser.ExecuteAsync(request).AsTask());
            Assert.Single(competing, result => result.ErrorCode is null);
            Assert.Single(competing, result => result.ErrorCode == "browser_target_changed");
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(request)).ErrorCode);
            var snapshot = new BrowserObserve(); var before = await browser.PrepareTenantCommandAsync(owner, snapshot);
            Assert.Null(before.ErrorCode); Assert.NotNull(before.Witness!.ContextId);
            await Dispatch(browser, owner, new BrowserNavigate(command.Url + "?new-document=true"));
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(new(owner.SessionId, snapshot) { TenantOwner = owner, TenantWitness = before.Witness })).ErrorCode);
            var current = await browser.PrepareTenantCommandAsync(owner, snapshot);
            var sibling = owner with { SessionId = Guid.NewGuid() }; browser.BindTenantSession(sibling);
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(new(sibling.SessionId, snapshot) { TenantOwner = sibling, TenantWitness = current.Witness })).ErrorCode);
            Assert.Null(browser.ContextFor(sibling.SessionId));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_shared_context_activity_isolation_changes_release_and_provider_restart_invalidate_old_command_targets()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-target-restart-" + Guid.NewGuid().ToString("N"));
        var owner = Owner(); var sibling = owner with { SessionId = Guid.NewGuid() }; var browser = Native(root);
        TenantBrowserCommandWitness old; var snapshot = new BrowserObserve();
        await browser.StartAsync(CancellationToken.None);
        try
        {
            await Open(browser, owner); await Open(browser, sibling);
            var prepared = await browser.PrepareTenantCommandAsync(owner, snapshot); Assert.Null(prepared.ErrorCode);
            await Dispatch(browser, sibling, new BrowserObserve());
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(new(owner.SessionId, snapshot) { TenantOwner = owner, TenantWitness = prepared.Witness })).ErrorCode);
            var beforeRelease = await browser.PrepareTenantCommandAsync(owner, snapshot);
            await browser.ReleaseTenantAsync(owner);
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(new(owner.SessionId, snapshot) { TenantOwner = owner, TenantWitness = beforeRelease.Witness })).ErrorCode);
            var cold = owner with { SessionId = Guid.NewGuid() }; browser.BindTenantSession(cold);
            Assert.Null(await browser.BindTenantIsolationAsync(cold, BrowserProfileMode.EphemeralSession));
            var coldCommand = new BrowserGetConfig(); var beforeMode = await browser.PrepareTenantCommandAsync(cold, coldCommand);
            Assert.Null(await browser.BindTenantIsolationAsync(cold, BrowserProfileMode.PersistentAgent));
            Assert.Null(await browser.BindTenantIsolationAsync(cold, BrowserProfileMode.EphemeralSession));
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(new(cold.SessionId, coldCommand) { TenantOwner = cold, TenantWitness = beforeMode.Witness })).ErrorCode);
            old = (await browser.PrepareTenantCommandAsync(owner, new BrowserGetConfig())).Witness!;
        }
        finally { await browser.StopAsync(CancellationToken.None); }
        var next = Native(root); await next.StartAsync(CancellationToken.None);
        try
        {
            Assert.Null(await next.BindTenantIsolationAsync(owner, old.ProfileMode));
            Assert.Equal("browser_target_changed", (await next.ExecuteAsync(new(owner.SessionId, new BrowserGetConfig()) { TenantOwner = owner, TenantWitness = old })).ErrorCode);
            Assert.Null(next.ContextFor(owner.SessionId));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next.PrepareTenantCommandAsync(owner, snapshot, cancelled.Token).AsTask());
        }
        finally { await next.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_target_preparation_wait_is_bounded_and_cancellation_does_not_retire_the_admitted_native_call()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-target-busy-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.InitializationProbe = async (stage, _) => { if (stage == "page") { entered.TrySetResult(); await release.Task; } };
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); Assert.Null(await browser.BindTenantIsolationAsync(owner, BrowserProfileMode.EphemeralSession));
            var command = new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html");
            var target = await browser.PrepareTenantCommandAsync(owner, command);
            var pending = browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = target.Witness }).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var cancellation = new CancellationTokenSource();
            var cancelled = browser.PrepareTenantCommandAsync(owner, new BrowserObserve(), cancellation.Token).AsTask(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal("browser_target_busy", (await browser.PrepareTenantCommandAsync(owner, new BrowserObserve()).AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ErrorCode);
            Assert.False(pending.IsCompleted); release.SetResult(); Assert.Null((await pending.WaitAsync(TimeSpan.FromSeconds(10))).ErrorCode);
            Assert.Null((await browser.PrepareTenantCommandAsync(owner, new BrowserObserve())).ErrorCode);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_uncertain_close_consumes_its_physical_target_and_never_replays_the_native_attempt()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-target-close-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); await Open(browser, owner);
            var attempts = 0;
            browser.ExplicitCloseProbe = _ => { Interlocked.Increment(ref attempts); throw new Microsoft.Playwright.PlaywrightException("Controlled lost close acknowledgement."); };
            var command = new BrowserClose(); var prepared = await browser.PrepareTenantCommandAsync(owner, command);
            var request = new BrowserRequest(owner.SessionId, command) { TenantOwner = owner, TenantWitness = prepared.Witness };
            var uncertain = await browser.ExecuteAsync(request); Assert.Equal("close_failed", uncertain.ErrorCode); Assert.True(uncertain.EffectAttempted);
            Assert.Equal("browser_target_changed", (await browser.ExecuteAsync(request)).ErrorCode); Assert.Equal(1, attempts);
            Assert.NotNull(browser.ContextFor(owner.SessionId));
        }
        finally { browser.ExplicitCloseProbe = null; await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TenantChromiumFact]
    public async Task Tenant_dialog_target_is_exact_and_pending_native_actions_keep_dialog_controls_usable()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenant-browser-target-dialog-" + Guid.NewGuid().ToString("N")); var browser = Native(root);
        await browser.StartAsync(CancellationToken.None);
        try
        {
            var owner = Owner(); await Open(browser, owner); var page = Page(browser, owner);
            var seen = new TaskCompletionSource<Microsoft.Playwright.IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Dialog += (_, dialog) => seen.TrySetResult(dialog);
            var first = page.EvaluateAsync<bool>("() => confirm('First dialog')");
            var firstDialog = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var command = new BrowserDialog("dismiss"); var before = await browser.PrepareTenantCommandAsync(owner, command);
            Assert.Null(before.ErrorCode); Assert.NotNull(before.Witness!.DialogId);
            Task<bool>? second = null;
            browser.TenantTargetValidatedProbe = async () => {
                browser.TenantTargetValidatedProbe = null;
                await firstDialog.DismissAsync(); Assert.False(await first);
                seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
                second = page.EvaluateAsync<bool>("() => confirm('Second dialog')");
                await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            };
            var stale = await browser.ExecuteAsync(new(owner.SessionId, command) { TenantOwner = owner, TenantWitness = before.Witness });
            Assert.Equal("browser_target_changed", stale.ErrorCode); Assert.False(stale.EffectAttempted);
            var after = await browser.PrepareTenantCommandAsync(owner, command); Assert.Null(after.ErrorCode);
            Assert.NotEqual(before.Witness.DialogId, after.Witness!.DialogId);
            Assert.Null((await Dispatch(browser, owner, command)).ErrorCode); Assert.False(await second!);
            Assert.Equal("dialog_pending", (await Dispatch(browser, owner, new BrowserClick(new("role", "button", "Confirm")))).ErrorCode);
            Assert.Equal("browser_target_busy", (await browser.PrepareTenantCommandAsync(owner, new BrowserObserve())).ErrorCode);
            Assert.Null((await Dispatch(browser, owner, new BrowserDialog("inspect"))).ErrorCode);
            Assert.Null((await Dispatch(browser, owner, command)).ErrorCode);
            Assert.Equal("Dismissed", await page.EvaluateAsync<string>("document.querySelector('#result').textContent"));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
