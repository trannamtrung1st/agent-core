using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserSemanticActionTests
{
    [Fact]
    public async Task Snapshot_then_direct_actions_verify_real_dom_without_discovery_and_deny_duplicates_and_secrets()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions
        {
            Enabled = true, Headless = true, FixtureEnabled = true, FixturePort = 0,
            InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"],
            InteractionOrigins = ["http://127.0.0.1:5091"]
        }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            async Task<BrowserResult> Run(string tool, string json) => await browser.ExecuteAsync(
                BrowserToolArguments.Request(id, tool, JsonSerializer.Deserialize<JsonElement>(json)));
            var opened = await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")));
            Assert.Null(opened.ErrorCode);
            Assert.DoesNotContain("[ref=", opened.Observation!.Content);
            Assert.Empty(opened.Observation.Targets);
            var page = browser.ContextFor(id)!.Pages[0];
            var changed = await Run("browser.type", """{"target":{"by":"role","value":"textbox","name":"Title"},"text":"Pump 002"}""");
            Assert.Null(changed.ErrorCode);
            Assert.True(changed.EffectAttempted); Assert.True(changed.EffectConfirmedBySdk); Assert.False(changed.ApplicationOutcomeVerified);
            var verified = await Run("browser.verify", """{"target":{"by":"role","value":"textbox","name":"Title"},"condition":"value","value":"Pump 002"}""");
            Assert.Null(verified.ErrorCode); Assert.True(verified.ApplicationOutcomeVerified); Assert.False(verified.EffectAttempted);
            Assert.Null((await Run("browser.click", """{"target":{"by":"role","value":"treeitem","name":"Asset 2"}}""")).ErrorCode);
            Assert.Equal("Pump 002", await page.GetByRole(AriaRole.Textbox, new() { Name = "Title" }).InputValueAsync());
            Assert.Equal("Selected Asset 2", await page.GetByRole(AriaRole.Status).InnerTextAsync());
            await page.EvaluateAsync("""() => { document.body.insertAdjacentHTML('beforeend', '<table><tr><td>Pump 001</td><td><button>Edit</button></td></tr><tr><td>Pump 002</td><td><button>Edit</button></td></tr></table>'); document.querySelector('table').addEventListener('click', e => { if(e.target.tagName === 'BUTTON') e.target.textContent = 'Edited'; }); }""");
            Assert.Equal("ambiguous_target", (await Run("browser.click", """{"target":{"by":"role","value":"button","name":"Edit"}}""")).ErrorCode);
            Assert.Equal(2, await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).CountAsync());
            Assert.Null((await Run("browser.click", """{"target":{"by":"role","value":"button","name":"Edit","within":{"by":"role","value":"row","hasText":"Pump 002"}}}""")).ErrorCode);
            Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "Edited" }).CountAsync());
            await page.EvaluateAsync("""() => document.body.insertAdjacentHTML('beforeend', '<label>Password<input type="password"></label><label>Token<input name="access_token"></label>')""");
            Assert.Equal("forbidden", (await Run("browser.type", """{"target":{"by":"label","value":"Password"},"text":"unsafe"}""")).ErrorCode);
            Assert.Equal("forbidden", (await Run("browser.type", """{"target":{"by":"label","value":"Token"},"text":"unsafe"}""")).ErrorCode);
            Assert.Equal("", await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            var secret = "protected-value-7824";
            var filled = await browser.FillCredentialAsync(id, new("label", "Password"), (origin, _) =>
            {
                Assert.Equal(browser.HostPolicy.NavigationOrigins.Single(), origin);
                return ValueTask.FromResult(secret);
            });
            Assert.Null(filled.ErrorCode);
            Assert.DoesNotContain(secret, filled.Observation!.Content);
            Assert.Equal(secret, await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            Assert.Equal("target_missing", (await Run("browser.click", """{"target":{"by":"role","value":"button","name":"Missing"}}""")).ErrorCode);
            Assert.False(BrowserToolArguments.TryRequest(id, "browser.click", JsonSerializer.Deserialize<JsonElement>("""{"ref":"el_bbbbbbbbbbbbbbbbbbbbbb"}"""), out _, out _));
            var closed = await Run("browser.close", "{}");
            Assert.Equal("closed", closed.Status);
            Assert.Null(browser.ContextFor(id));
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }
    [Fact]
    public async Task Returning_to_a_tab_requires_its_current_frame_inventory_before_an_effect()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true,
            FixtureEnabled = true, FixturePort = 0, InteractionMode = "InteractiveDemo",
            NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1";
            var opened = await browser.ExecuteAsync(new(id, new BrowserNavigate(url)));
            Assert.Null(opened.ErrorCode);
            var original = browser.ContextFor(id)!.Pages[0];
            await original.FrameLocator("iframe").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            var observed = (await browser.ExecuteAsync(new(id, new BrowserObserve()))).Observation!;
            var oldFrame = Assert.Single(observed.Frames!).Ref;
            var target = new BrowserTarget("role", "button", Name: "Save frame", FrameRef: oldFrame);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserTabs("new", Url: url)))).ErrorCode);
            var returned = await browser.ExecuteAsync(new(id, new BrowserTabs("select", TabRef: observed.TabRef)));
            Assert.Null(returned.ErrorCode);
            Assert.Equal("stale_frame", (await browser.ExecuteAsync(new(id, new BrowserClick(target)))).ErrorCode);
            Assert.DoesNotContain("Frame saved", await original.FrameLocator("iframe").Locator("body").InnerTextAsync());
            var currentFrame = Assert.Single(returned.Observation!.Frames!).Ref;
            Assert.NotEqual(oldFrame, currentFrame);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(target with { FrameRef = currentFrame })))).ErrorCode);
            Assert.Contains("Frame saved", await original.FrameLocator("iframe").Locator("body").InnerTextAsync());
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

}
