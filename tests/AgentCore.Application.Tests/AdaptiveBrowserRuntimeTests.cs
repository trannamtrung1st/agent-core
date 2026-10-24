using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Application.Tests;

public sealed class AdaptiveBrowserRuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Browser_strategy_is_projected_only_for_eligible_authority(bool authorized, bool vision)
    {
        var names = authorized ? new[] { ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserVisionMouse } : [];
        var definition = await CapabilityProjectionTests.Definition(names);
        var context = CapabilityProjectionTests.Context(definition) with { ModelSupportsTools = true, ModelSupportsVision = vision };
        var request = new PromptContextBuilder(ToolConfigurationGates.AllowAll).Build(context, Guid.NewGuid());
        var guidance = request.Messages.Where(m => m.Text.StartsWith("Browser workflow:")).ToArray();
        if (!authorized) Assert.Empty(guidance);
        else
        {
            var text = Assert.Single(guidance).Text;
            if (vision) { Assert.Contains("proactively", text); Assert.Contains("snapshotId", text); }
            else { Assert.Contains("never claim visual understanding", text); Assert.DoesNotContain("proactively", text); }
        }
    }

    [Fact]
    public async Task Host_disabled_capture_is_not_recommended_to_a_vision_model()
    {
        var browser = new NativePlaywrightBrowser(new() { ScreenshotPrivacy = "Disabled" }, null);
        var definition = await CapabilityProjectionTests.Definition([ToolCatalog.BrowserSnapshot, ToolCatalog.BrowserScreenshot, ToolCatalog.BrowserVisionMouse]);
        var context = CapabilityProjectionTests.Context(definition) with { ModelSupportsTools = true, ModelSupportsVision = true };
        var request = new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser).Build(context, Guid.NewGuid());
        var text = Assert.Single(request.Messages, m => m.Text.StartsWith("Browser workflow:")).Text;
        Assert.Contains("Host policy disables screenshots", text);
        Assert.DoesNotContain("proactively", text);
        Assert.Contains("visual coordinates are unavailable", text);
    }

    internal static NativePlaywrightBrowser NewBrowser() => new(new BrowserOptions { Enabled = true, Headless = true, FixtureEnabled = true,
        FixturePort = 0, InteractionMode = "InteractiveDemo", NavigationOrigins = ["http://127.0.0.1:5091"], InteractionOrigins = ["http://127.0.0.1:5091"] }, loggerFactory: null);


}
