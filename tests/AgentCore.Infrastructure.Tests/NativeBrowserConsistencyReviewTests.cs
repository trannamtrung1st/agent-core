using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Tests.Shared;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserConsistencyReviewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typing_rechecks_protection_before_sequential_keys_or_submission(bool slowly)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<input data-testid='field' aria-label='Review field' value='initial'>");
            await page.EvaluateAsync("""
                () => {
                    const field = document.querySelector('input');
                    window.enterCount = 0;
                    field.addEventListener('input', () => field.type = 'password');
                    field.addEventListener('keydown', e => { if (e.key === 'Enter') window.enterCount++; });
                }
                """);
            var found = await browser.ExecuteAsync(new(id, BrowserOperation.Find, new() { Query = new() { TestId = "field" } }));
            Assert.Null(found.ErrorCode);
            using var data = JsonDocument.Parse(found.DataJson!);
            var reference = data.RootElement.GetProperty("matches")[0].GetProperty("ref").GetString()!;
            var result = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.type",
                JsonSerializer.SerializeToElement(new { @ref = reference, text = "review value", slowly, submit = !slowly })));
            Assert.Equal("forbidden", result.ErrorCode);
            Assert.Equal(slowly ? "" : "review value", await page.GetByTestId("field").InputValueAsync());
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.enterCount"));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("text", true)]
    [InlineData("textGone", false)]
    public async Task Text_wait_checks_visible_matches_beyond_the_first(string condition, bool succeeds)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<p hidden>Review wait marker</p><p>Review wait marker</p>");
            var result = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.wait_for",
                JsonSerializer.SerializeToElement(new { condition, text = "Review wait marker", timeoutMs = 300 })));
            if (succeeds) Assert.Null(result.ErrorCode);
            else Assert.Equal("timeout", result.ErrorCode);

            await page.SetContentAsync("<p hidden>Review wait marker</p><p hidden>Review wait marker</p>");
            Assert.Null((await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.wait_for",
                JsonSerializer.SerializeToElement(new { condition = "textGone", text = "Review wait marker", timeoutMs = 300 })))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Theory]
    [InlineData("password", "forbidden")]
    [InlineData("otp", "forbidden")]
    [InlineData("duplicate", "ambiguous_target")]
    [InlineData("ordinary", null)]
    public async Task Form_rechecks_later_fields_after_an_earlier_input_changes_the_page(string mutation, string? expectedError)
    {
        var browser = new NativePlaywrightBrowser(new() { Enabled = true, Headless = true, FixturePort = 0 }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(BrowserTestRequests.Navigate(id,
                new Uri(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.SetContentAsync("<input data-testid='first' aria-label='First field'><input data-testid='second' aria-label='Second field'>");
            await page.EvaluateAsync("""
                mutation => document.querySelector('[data-testid=first]').addEventListener('input', () => {
                    const later = document.querySelector('[data-testid=second]');
                    if (mutation === 'password') later.type = 'password';
                    else if (mutation === 'otp') later.name = 'otp';
                    else if (mutation === 'ordinary') later.replaceWith(later.cloneNode());
                    else later.after(later.cloneNode());
                })
                """, mutation);
            async Task<string> Find(string testId)
            {
                var found = await browser.ExecuteAsync(new(id, BrowserOperation.Find,
                    new() { Query = new() { TestId = testId } }));
                Assert.Null(found.ErrorCode);
                using var data = JsonDocument.Parse(found.DataJson!);
                return data.RootElement.GetProperty("matches")[0].GetProperty("ref").GetString()!;
            }
            var first = await Find("first");
            var second = await Find("second");
            var result = await browser.ExecuteAsync(BrowserToolArguments.Request(id, "browser.fill_form",
                JsonSerializer.SerializeToElement(new { fields = new[] { new { @ref = first, value = "change page" }, new { @ref = second, value = "later value" } } })));
            Assert.Equal(expectedError, result.ErrorCode);
            Assert.Equal("change page", await page.GetByTestId("first").InputValueAsync());
            Assert.Equal(expectedError is null ? "later value" : "", await page.GetByTestId("second").First.InputValueAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
}
