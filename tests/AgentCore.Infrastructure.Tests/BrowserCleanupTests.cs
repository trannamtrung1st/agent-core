using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCleanupTests
{
    [Fact]
    public async Task Signout_confirmation_failure_dismissal_and_acceptance_preserve_independent_authentication_state()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var page = browser.ContextFor(id)!.Pages[0];
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            async Task Pending()
            {
                var target = await BrowserTestQueries.Find(browser, id, "Sign out");
                Assert.Equal("dialog_pending", (await Run(ToolCatalog.BrowserClick, new { @ref = target.Ref })).ErrorCode);
                Assert.Equal("dialog_pending", (await browser.ExecuteAsync(BrowserTestRequests.Inspect(id))).ErrorCode);
                var inspected = await Run(ToolCatalog.BrowserDialog, new { operation = "inspect" });
                Assert.Contains("confirm", inspected.DataJson!);
                Assert.DoesNotContain("Sign out of", inspected.DataJson!);
            }
            async Task<bool> Authenticated() => await page.EvaluateAsync<bool>("() => localStorage.getItem('fixture-authenticated') === 'yes'");
            await Pending();
            browser.DialogResolutionProbe = _ => throw new PlaywrightException("Injected resolution failure");
            Assert.NotNull((await Run(ToolCatalog.BrowserDialog, new { operation = "accept" })).ErrorCode);
            Assert.Null((await Run(ToolCatalog.BrowserDialog, new { operation = "inspect" })).ErrorCode);
            browser.DialogResolutionProbe = null;
            Assert.Null((await Run(ToolCatalog.BrowserDialog, new { operation = "dismiss" })).ErrorCode);
            Assert.True(await Authenticated());
            await Pending();
            Assert.Null((await Run(ToolCatalog.BrowserDialog, new { operation = "accept" })).ErrorCode);
            Assert.False(await Authenticated());
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).IsVisibleAsync());
            Assert.Equal("closed", (await Run(ToolCatalog.BrowserClose, new { })).Status);
            Assert.True(page.IsClosed);
            Assert.Equal("already_closed", (await Run(ToolCatalog.BrowserClose, new { })).Status);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
        ValueTask<AgentCore.Application.Ports.BrowserResult> Run(string name, object args) => browser.ExecuteAsync(BrowserToolArguments.Request(id, name, JsonSerializer.SerializeToElement(args)));
    }

    [Fact]
    public async Task Stalled_native_close_is_bounded_and_late_confirmation_releases_the_context()
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid(); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login")));
            var context = browser.ContextFor(id)!;
            browser.OperationTimeout = TimeSpan.FromMilliseconds(100);
            browser.ExplicitCloseProbe = async value => { await release.Task; await value.CloseAsync(); closed.TrySetResult(); };
            Assert.Equal("close_uncertain", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).ErrorCode);
            Assert.Same(context, browser.ContextFor(id));
            Assert.False(context.Pages[0].IsClosed);
            release.TrySetResult(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(browser.ContextFor(id));
            Assert.Equal("already_closed", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).Status);
        }
        finally { release.TrySetResult(); await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_close_requires_native_confirmation_and_retains_persistent_ownership_on_failure(bool uncertain)
    {
        var root = Path.Combine(Path.GetTempPath(), "browser-close-" + Guid.NewGuid().ToString("N"));
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0, ProfileMode = "PersistentAgent", ProfileRoot = root }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid(); var owner = Guid.NewGuid(); browser.BindSession(id, owner);
        try
        {
            var url = new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/credential-login");
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, url));
            var context = browser.ContextFor(id)!; var page = context.Pages[0];
            await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
            browser.ExplicitCloseProbe = _ => uncertain ? Task.CompletedTask : throw new PlaywrightException("Injected close failure");
            var failed = await browser.ExecuteAsync(BrowserTestRequests.Close(id));
            Assert.Equal(uncertain ? "close_uncertain" : "close_failed", failed.ErrorCode);
            Assert.False(page.IsClosed); Assert.Same(context, browser.ContextFor(id));
            await Assert.ThrowsAsync<IOException>(() => browser.ResetPersistentProfileAsync(owner).AsTask());
            Assert.Same(context, browser.ContextFor(id));
            browser.ExplicitCloseProbe = null;
            Assert.Equal("closed", (await browser.ExecuteAsync(BrowserTestRequests.Close(id))).Status);
            Assert.True(page.IsClosed);
            await browser.ExecuteAsync(BrowserTestRequests.Navigate(id, url));
            var reopened = browser.ContextFor(id)!.Pages[0];
            Assert.True(await reopened.GetByRole(AriaRole.Button, new() { Name = "Sign out", Exact = true }).IsVisibleAsync());
            Assert.Equal("yes", await reopened.EvaluateAsync<string>("() => localStorage.getItem('fixture-authenticated')"));
        }
        finally { await browser.StopAsync(CancellationToken.None); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
