using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class BrowserV2BoundaryReviewTests
{
    [Theory]
    [InlineData("otp")]
    [InlineData("passcode")]
    [InlineData("apikey")]
    public async Task Protected_field_metadata_is_rechecked_for_native_typed_and_coordinate_actions(string protectedName)
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        async Task<BrowserCommandResult> Run(string tool, object args) => await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(args)));
        try
        {
            var opened = await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")));
            Assert.Null(opened.ErrorCode);
            var reference = opened.Observation!.Elements.Single(e => e.Name == "Title").Ref;
            var page = browser.ContextFor(id)!.Pages[0];
            var field = page.GetByRole(AriaRole.Textbox, new() { Name = "Title", Exact = true });
            await field.FillAsync("original");
            // Retain the accessible name/Locator while the SPA makes this field protected.
            await field.EvaluateAsync("(el,name) => el.name=name", protectedName);
            Assert.Equal("unsupported_operation", (await Run("browser.type", new { @ref = reference, text = "replacement", slowly = true })).ErrorCode);
            var typed = await browser.InteractAsync(new(id, "fill", reference, "replacement"));
            Assert.Equal("unsupported_operation", typed.ErrorCode);
            Assert.Empty(typed.AllowedActions!);
            Assert.Equal("unsupported_operation", (await Run("browser.press_key", new { @ref = reference, key = "Backspace" })).ErrorCode);
            var dragSource = opened.Observation.Elements.First(e => e.Actions.Contains("click")).Ref;
            Assert.Equal("unsupported_operation", (await browser.InteractAsync(new(id, "drag", dragSource, null, TargetRef: reference))).ErrorCode);
            Assert.Equal("unsupported_operation", (await Run("browser.fill_form", new { fields = new[] { new { @ref = reference, value = "replacement" } } })).ErrorCode);
            Assert.Equal("forbidden", (await Run("browser.verify", new { @ref = reference, condition = "value", value = "original" })).ErrorCode);
            await field.FocusAsync();
            Assert.Equal("forbidden", (await Run("browser.press_key", new { key = "Backspace" })).ErrorCode);
            Assert.Equal("original", await field.InputValueAsync());
            await field.ScrollIntoViewIfNeededAsync();
            var box = (await field.BoundingBoxAsync())!;
            Assert.Equal("target_denied", (await Run("browser.mouse", new { operation = "click", x = box.X + box.Width / 2, y = box.Y + box.Height / 2 })).ErrorCode);
            var recovered = await browser.SnapshotAsync(id);
            Assert.Null(recovered.ErrorCode);
            Assert.DoesNotContain(recovered.Observation!.Elements, e => e.Name == "Title");
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Storage_redacts_decoded_values_before_serialization_and_truncation(bool longValue)
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            foreach (var tool in new[] { "browser.local_storage", "browser.session_storage" })
            {
                var value = longValue ? new string('z', 300) : "private-quote-\"line\n<&>\\9374";
                await page.EvaluateAsync("a => { const s=a.session?sessionStorage:localStorage; s.clear(); s.setItem('review',a.value); }",
                    new { session = tool == "browser.session_storage", value });
                foreach (var key in new[] { "review" })
                {
                    var result = await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(new { operation = "get", key })));
                    Assert.Null(result.ErrorCode);
                    using var data = JsonDocument.Parse(result.DataJson!);
                    Assert.Equal("[redacted]", data.RootElement.GetProperty("value").GetString());
                }
                var listed = await browser.ExecuteAsync(new(id, tool, JsonSerializer.SerializeToElement(new { operation = "list" })));
                using var list = JsonDocument.Parse(listed.DataJson!);
                Assert.All(list.RootElement.GetProperty("items").EnumerateArray(), item => Assert.Equal("[redacted]", item.GetProperty("value").GetString()));
            }
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Canceled_storage_mutation_closes_the_pending_page_before_recovery()
    {
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var url = new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-v2.html?compact=1");
            Assert.Null((await browser.NavigateAsync(new(id, url))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Console += (_, message) => { if (message.Text == "storage-review-started") started.TrySetResult(); };
            // A non-cooperative native mutation remains pending until the provider fences its page.
            await page.EvaluateAsync("() => { Storage.prototype.setItem = function() { console.log('storage-review-started'); while(true) {} }; }");
            using var cancel = new CancellationTokenSource();
            var pending = browser.ExecuteAsync(new(id, "browser.local_storage", JsonSerializer.SerializeToElement(new { operation = "set", key = "draft", value = "replacement" })), cancel.Token).AsTask();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.True(page.IsClosed);
            Assert.Null((await browser.NavigateAsync(new(id, url))).ErrorCode);
            Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Cookie_delete_and_clear_cover_domain_cookies_without_removing_another_host()
    {
        await using var fixture = new LoopbackBrowserFixtureHost(NullLogger.Instance);
        await fixture.StartAsync(0, CancellationToken.None);
        var origin = "http://app.review.test";
        var browser = new PlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = false,
            NavigationOrigins = [fixture.Origin!, origin], InteractionOrigins = [fixture.Origin!, origin] }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        async Task<BrowserCommandResult> Run(object args) => await browser.ExecuteAsync(new(id, "browser.cookies", JsonSerializer.SerializeToElement(args)));
        try
        {
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(fixture.Origin + "/browser-v2.html?compact=1")))).ErrorCode);
            var context = browser.ContextFor(id)!;
            // Fulfill this test domain locally: no DNS or external network is involved.
            await context.RouteAsync(origin + "/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<html><body><button>Ready</button></body></html>" }));
            Assert.Null((await browser.NavigateAsync(new(id, new Uri(origin)))).ErrorCode);
            await context.AddCookiesAsync([
                new() { Name = "parent", Value = "parent-value", Domain = ".review.test", Path = "/" },
                new() { Name = "nested", Value = "nested-value", Domain = ".review.test", Path = "/account" },
                new() { Name = "other", Value = "other-value", Domain = "other.review.test", Path = "/" }
            ]);
            Assert.Contains(await context.CookiesAsync(), c => c.Name == "parent" && c.Domain == ".review.test");
            var listed = await Run(new { operation = "list" });
            Assert.Null(listed.ErrorCode);
            using (var data = JsonDocument.Parse(listed.DataJson!))
                Assert.Equal(new[] { "nested", "parent" }, data.RootElement.GetProperty("cookies").EnumerateArray().Select(c => c.GetProperty("name").GetString()).Order().ToArray());
            Assert.Null((await Run(new { operation = "delete", name = "parent" })).ErrorCode);
            Assert.DoesNotContain(await context.CookiesAsync(), c => c.Name == "parent");
            Assert.Null((await Run(new { operation = "clear" })).ErrorCode);
            var retained = Assert.Single(await context.CookiesAsync());
            Assert.Equal("other", retained.Name);
            var reflected = new string('z', 300);
            await context.AddCookiesAsync([new() { Name = reflected, Value = reflected, Domain = ".review.test", Path = "/" + reflected }]);
            var metadata = await Run(new { operation = "list" });
            using (var data = JsonDocument.Parse(metadata.DataJson!))
            {
                var cookie = Assert.Single(data.RootElement.GetProperty("cookies").EnumerateArray());
                Assert.Equal("[redacted]", cookie.GetProperty("name").GetString());
                Assert.Equal("/[redacted]", cookie.GetProperty("path").GetString());
            }
            Assert.Null((await browser.SnapshotAsync(id)).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
