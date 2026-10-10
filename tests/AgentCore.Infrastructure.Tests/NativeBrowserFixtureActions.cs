using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

internal static class NativeBrowserFixtureActions
{
    // Fixture setup that starts network/navigation work must run inside a native
    // operation, so its requests share the same deadline and cancellation fence.
    public static async Task ApplyAsync(NativePlaywrightBrowser browser, Guid sessionId, IFrame frame, string script, object? argument = null)
    {
        const string name = "Apply fixture navigation";
        await frame.EvaluateAsync($$"""
            arg => { const button = document.createElement('button'); button.textContent = '{{name}}';
              button.setAttribute('data-fixture-navigation', '');
              button.onclick = () => ({{script}})(arg); document.body.appendChild(button); }
            """, argument);
        var page = frame.Page;
        var observation = (await browser.ExecuteAsync(new(sessionId, new BrowserObserve()))).Observation!;
        var frameRef = frame == page.MainFrame ? null : observation.Frames!.Single(f => f.Url == frame.Url).Ref;
        var result = await browser.ExecuteAsync(new(sessionId, new BrowserClick(new("role", "button", Name: name, FrameRef: frameRef))));
        Assert.Null(result.ErrorCode);
        if (!frame.IsDetached) await frame.Locator("[data-fixture-navigation]").EvaluateAllAsync<bool>("elements => { elements.forEach(element => element.remove()); return true; }");
    }
}
