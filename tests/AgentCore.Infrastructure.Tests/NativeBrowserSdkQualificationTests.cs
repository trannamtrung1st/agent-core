using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class NativeBrowserSdkQualificationTests
{
    [Fact]
    public async Task Published_sdk_supports_direct_semantics_and_ai_snapshots_without_public_ref_resolution()
    {
        Assert.Null(typeof(IPage).GetMethod("GetByRef")); // Public API inventory only; no private SDK access.
        using var driver = await Playwright.CreateAsync();
        await using var browser = await driver.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <label>Email<input placeholder="Your email"></label>
            <label>Password<input type="password"></label>
            <button onclick="this.textContent='Saved'">Save</button>
            <table><tr><td>Pump 001</td><td><button>Edit</button></td></tr>
            <tr><td>Pump 002</td><td><button onclick="this.textContent='Edited'">Edit</button></td></tr></table>
            <input data-testid="serial"><div id="shadow"></div>
            <iframe srcdoc="<button>Frame button</button>"></iframe>
            <script>document.querySelector('#shadow').attachShadow({mode:'open'}).innerHTML='<button>Shadow button</button>';</script>
            """);
        var ai = await page.AriaSnapshotAsync(new() { Mode = AriaSnapshotMode.Ai, Depth = 32 });
        Assert.Contains("[ref=", ai);
        Assert.Contains("Frame button", ai); // AI snapshots cross frame boundaries: unsuitable for unfiltered production output.
        var ordinary = await page.Locator("body").AriaSnapshotAsync(new() { Depth = 32 });
        Assert.DoesNotContain("[ref=", ordinary);
        Assert.DoesNotContain("Frame button", ordinary);
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Email" }).FillAsync("sample@example.test");
        await page.GetByPlaceholder("Your email").FillAsync("placeholder@example.test");
        await page.GetByLabel("Email").FillAsync("label@example.test");
        await page.GetByTestId("serial").FillAsync("P002");
        Assert.Equal(2, await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).CountAsync());
        await Assert.ThrowsAsync<PlaywrightException>(() => page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync());
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Pump 002" })
            .GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
        var save = page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true });
        await page.EvaluateAsync("""() => { const b = document.createElement('button'); b.textContent = 'Save'; b.onclick = () => b.textContent = 'Saved'; document.querySelector('button').replaceWith(b); }""");
        await save.ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Shadow button" }).ClickAsync();
        Assert.Equal("label@example.test", await page.GetByLabel("Email").InputValueAsync());
        Assert.Equal("P002", await page.GetByTestId("serial").InputValueAsync());
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "Edited" }).CountAsync());
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "Saved" }).CountAsync());
        Assert.Equal(1, await page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).CountAsync());
    }
}
