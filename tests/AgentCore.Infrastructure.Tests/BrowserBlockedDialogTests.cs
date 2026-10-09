using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserBlockedDialogTests
{
    [Fact]
    public async Task Successive_dialogs_preserve_the_new_decision_and_do_not_replay_the_click()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.EvaluateAsync("() => { document.body.innerHTML = '<button>Two decisions</button>'; window.effects = 0; document.querySelector('button').onclick = () => { window.effects++; confirm('private first'); prompt('private second'); }; }");
            var target = await BrowserTestQueries.Find(browser, id, "Two decisions");
            Assert.Equal("dialog_pending", (await Run(ToolCatalog.BrowserClick, new { @ref = target.Ref })).ErrorCode);
            Assert.Equal("dialog_pending", (await Run(ToolCatalog.BrowserDialog, new { operation = "accept" })).ErrorCode);
            var inspect = await Run(ToolCatalog.BrowserDialog, new { operation = "inspect" });
            Assert.Contains("prompt", inspect.DataJson!);
            Assert.DoesNotContain("private", inspect.DataJson!);
            Assert.Null((await Run(ToolCatalog.BrowserDialog, new { operation = "dismiss" })).ErrorCode);
            Assert.Equal(1, await page.EvaluateAsync<int>("() => window.effects"));
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
        ValueTask<BrowserResult> Run(string tool, object args) => browser.ExecuteAsync(BrowserToolArguments.Request(id, tool, JsonSerializer.SerializeToElement(args)));
    }

    [Fact]
    public async Task Externally_handled_dialog_returns_missing_on_resolution_and_restores_page_reads()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(id)!.Pages[0];
            var shown = new TaskCompletionSource<IDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Dialog += (_, dialog) => shown.TrySetResult(dialog);
            var pending = page.EvaluateAsync("() => alert('private stale message')");
            var dialog = await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await dialog.DismissAsync(); await pending;
            var resolve = await browser.ExecuteAsync(BrowserToolArguments.Request(id, ToolCatalog.BrowserDialog, JsonSerializer.SerializeToElement(new { operation = "accept" })));
            Assert.Equal("dialog_missing", resolve.ErrorCode);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cancellation_of_pending_dialog_resolution_fences_late_native_effect()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? native = null;
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            var target = await BrowserTestQueries.Find(browser, id, "Sign out");
            Assert.Equal("dialog_pending", (await Run(ToolCatalog.BrowserClick, new { @ref = target.Ref })).ErrorCode);
            browser.DialogResolutionProbe = dialog => native = Delayed(dialog);
            async Task Delayed(IDialog dialog) { entered.TrySetResult(); await release.Task; await dialog.AcceptAsync(); }
            using var cancel = new CancellationTokenSource();
            var pending = Run(ToolCatalog.BrowserDialog, new { operation = "accept" }, cancel.Token).AsTask();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(page.IsClosed);
            browser.DialogResolutionProbe = null;
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<PlaywrightException>(() => native!);
            Assert.Equal("dialog_missing", (await Run(ToolCatalog.BrowserDialog, new { operation = "inspect" })).ErrorCode);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); }
        ValueTask<BrowserResult> Run(string tool, object args, CancellationToken ct = default) => browser.ExecuteAsync(BrowserToolArguments.Request(id, tool, JsonSerializer.SerializeToElement(args)), ct);
    }

    [Fact]
    public async Task Closed_dialog_page_does_not_leave_a_stale_inspectable_dialog()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var origin = browser.HostPolicy.NavigationOrigins.Single();
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/credential-login")));
            var page = browser.ContextFor(id)!.Pages[0];
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Dialog += (_, _) => shown.TrySetResult();
            var pending = page.EvaluateAsync("() => confirm('private stale message')");
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await page.CloseAsync();
            try { await pending; } catch (PlaywrightException) { }
            var inspect = await browser.ExecuteAsync(BrowserToolArguments.Request(id, ToolCatalog.BrowserDialog, JsonSerializer.SerializeToElement(new { operation = "inspect" })));
            Assert.Equal("dialog_missing", inspect.ErrorCode);
            Assert.Equal("closed", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).Status);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(origin + "/credential-login")))).ErrorCode);
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
