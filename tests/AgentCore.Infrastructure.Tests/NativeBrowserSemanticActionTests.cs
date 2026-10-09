using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Execution;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserSemanticActionTests
{
    [Fact]
    public async Task Permitted_frame_roots_are_native_bounded_masked_and_generation_fenced()
    {
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true,
            FixtureEnabled = true, FixturePort = 0, InteractionMode = "InteractiveDemo",
            NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            var url = browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1";
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(url)))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.FrameLocator("iframe").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            var frame = page.Frames.Single(f => f != page.MainFrame);
            await frame.EvaluateAsync("""
            () => {
                document.body.insertAdjacentHTML('beforeend', '<label>Password<input type="password" value="frame-password-93742"></label><label>Token<input name="access_token" value="frame-token-93742"></label><p>frame-password-93742 frame-token-93742</p><iframe title="Denied nested" srcdoc="<h1>DENIED-NESTED-CONTENT</h1>"></iframe>');
            }
            """);
            await frame.FrameLocator("iframe").GetByRole(AriaRole.Heading).WaitForAsync();
            var main = (await browser.ExecuteAsync(new(id, new BrowserObserve()))).Observation!;
            Assert.DoesNotContain("Save frame", main.Content);
            var reference = Assert.Single(main.Frames!).Ref;
            var root = await browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: reference)));
            Assert.Null(root.ErrorCode);
            Assert.Equal(reference, root.Observation!.FrameRef);
            Assert.True(root.Observation.HasPasswordField);
            Assert.Contains("Save frame", root.Observation.Content);
            foreach (var forbidden in new[] { "frame-password-93742", "frame-token-93742", "DENIED-NESTED-CONTENT" })
                Assert.DoesNotContain(forbidden, JsonSerializer.Serialize(root));
            var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
            var definition = new AgentDefinition(1, "frame-test", 1, new("Frame", "Role", "Description", "Tone"), [], "Use direct browser targets.",
                new("answerNewTurn", true, true), new("balanced", false, "en", 2048),
                new(false, 60_000, 120_000, 1, ["longSilence"], 0), new(false, "default", 1),
                new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
                new RoleEnvironment(ToolAllowlist: [ToolCatalog.BrowserSnapshot]));
            var receipt = await executor.ExecuteAsync(definition, id, new("frame-root", ToolCatalog.BrowserSnapshot, JsonSerializer.Serialize(new { frameRef = reference })), ToolLimits.MaxOutputBytes,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
            var persisted = AgentRunToolCallCheckpoint.Write([new(ModelRole.Tool, receipt.Text, ToolCallId: "frame-root", Name: ToolCatalog.BrowserSnapshot)]);
            foreach (var forbidden in new[] { "frame-password-93742", "frame-token-93742", "DENIED-NESTED-CONTENT" })
                Assert.DoesNotContain(forbidden, persisted);
            Assert.Contains(reference, receipt.Text);
            var target = new BrowserTarget("role", "button", Name: "Save frame");
            var scoped = await browser.ExecuteAsync(new(id, new BrowserObserve(target, FrameRef: reference)));
            Assert.Null(scoped.ErrorCode);
            Assert.Equal(reference, scoped.Observation!.Scope!.FrameRef);
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserClick(target with { FrameRef = reference })))).ErrorCode);
            Assert.Contains("Frame saved", await frame.Locator("body").InnerTextAsync());
            await frame.EvaluateAsync("""() => {for(let i=0;i<1000;i++){const p=document.createElement('p');p.textContent='Bounded frame row '+i+' x'.repeat(80);document.body.append(p);}}""");
            var large = await browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: reference)));
            Assert.Null(large.ErrorCode);
            Assert.True(large.Observation!.ContentTruncated);
            Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(large.Observation.Content), 1, BrowserToolLimits.MaxSnapshotBytes);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: reference)), cancelled.Token).AsTask());
            await page.Locator("iframe").EvaluateAsync("(el) => el.src='/browser-native-frame.html?new=1'");
            await page.FrameLocator("iframe").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            Assert.Equal("stale_frame", (await browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: reference)))).ErrorCode);
            var next = Assert.Single((await browser.ExecuteAsync(new(id, new BrowserObserve()))).Observation!.Frames!).Ref;
            await page.Locator("iframe").EvaluateAsync("el => el.remove()");
            Assert.Equal("stale_frame", (await browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: next)))).ErrorCode);
            Assert.Empty((await browser.ExecuteAsync(new(id, new BrowserObserve()))).Observation!.Frames!);
            Assert.Equal("closed", (await browser.ExecuteAsync(new(id, new BrowserClose()))).Status);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Frame_inventory_excludes_cross_origin_and_denied_ancestor_interiors()
    {
        await using var deniedOrigin = new LoopbackBrowserFixtureHost(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await deniedOrigin.StartAsync(0, CancellationToken.None);
        var browser = new NativePlaywrightBrowser(new BrowserOptions { Enabled = true, Headless = true,
            FixtureEnabled = true, FixturePort = 0, InteractionMode = "InteractiveDemo",
            NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, null);
        await browser.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        try
        {
            Assert.Null((await browser.ExecuteAsync(new(id, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            var page = browser.ContextFor(id)!.Pages[0];
            await page.FrameLocator("iframe").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            var outer = page.Frames.Single(f => f != page.MainFrame);
            await outer.EvaluateAsync("""
            () => {
                const permitted = document.createElement('iframe'); permitted.title = 'Permitted nested'; permitted.src = '/browser-native-frame.html?nested=1'; document.body.append(permitted);
                const denied = document.createElement('iframe'); denied.title = 'Denied parent'; denied.srcdoc = '<h1>DENIED-ANCESTOR-TEXT</h1><iframe src="/browser-native-frame.html?denied-child=1"></iframe>'; document.body.append(denied);
            }
            """);
            await outer.FrameLocator("iframe[title='Permitted nested']").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            await outer.FrameLocator("iframe[title='Denied parent']").FrameLocator("iframe").GetByRole(AriaRole.Button, new() { Name = "Save frame" }).WaitForAsync();
            await page.EvaluateAsync("""url => { const frame = document.createElement('iframe'); frame.title = 'Cross-origin denied'; frame.src = url; document.body.append(frame); }""", deniedOrigin.Origin + "/browser-native-frame.html");
            var observation = (await browser.ExecuteAsync(new(id, new BrowserObserve()))).Observation!;
            Assert.Equal(2, observation.Frames!.Count);
            Assert.DoesNotContain("DENIED-ANCESTOR-TEXT", JsonSerializer.Serialize(observation));
            Assert.DoesNotContain(observation.Frames, f => f.Url.Contains("denied-child", StringComparison.Ordinal));
            var nested = Assert.Single(observation.Frames, f => f.Url.Contains("nested=1", StringComparison.Ordinal));
            var result = await browser.ExecuteAsync(new(id, new BrowserObserve(FrameRef: nested.Ref)));
            Assert.Null(result.ErrorCode);
            Assert.Contains("Save frame", result.Observation!.Content);
            Assert.DoesNotContain("DENIED-ANCESTOR-TEXT", JsonSerializer.Serialize(result));
            var foreign = Guid.NewGuid();
            Assert.Null((await browser.ExecuteAsync(new(foreign, new BrowserNavigate(browser.HostPolicy.NavigationOrigins.Single() + "/browser-native.html?compact=1")))).ErrorCode);
            Assert.Equal("stale_frame", (await browser.ExecuteAsync(new(foreign, new BrowserObserve(FrameRef: nested.Ref)))).ErrorCode);
        }
        finally { await browser.StopAsync(CancellationToken.None); }
    }

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
            Assert.True(opened.EffectAttempted); Assert.True(opened.EffectConfirmedBySdk); Assert.False(opened.ApplicationOutcomeVerified);
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
            Assert.True(filled.EffectAttempted); Assert.True(filled.EffectConfirmedBySdk); Assert.False(filled.ApplicationOutcomeVerified);
            Assert.DoesNotContain(secret, filled.Observation!.Content);
            Assert.Equal(secret, await page.GetByLabel("Password", new() { Exact = true }).InputValueAsync());
            Assert.Equal("target_missing", (await Run("browser.click", """{"target":{"by":"role","value":"button","name":"Missing"}}""")).ErrorCode);
            Assert.False(BrowserToolArguments.TryRequest(id, "browser.click", JsonSerializer.Deserialize<JsonElement>("""{"ref":"el_bbbbbbbbbbbbbbbbbbbbbb"}"""), out _, out _));
            var closed = await Run("browser.close", "{}");
            Assert.Equal("closed", closed.Status);
            Assert.True(closed.EffectAttempted); Assert.True(closed.EffectConfirmedBySdk); Assert.False(closed.ApplicationOutcomeVerified);
            Assert.Null(browser.ContextFor(id));
            var again = await Run("browser.close", "{}");
            Assert.Equal("already_closed", again.Status); Assert.False(again.EffectAttempted); Assert.False(again.EffectConfirmedBySdk);
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
