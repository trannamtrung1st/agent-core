using System.Text.Json;

namespace AgentCore.Api.Tests;

public sealed class BrowserLaunchProfileTests
{
    [Fact]
    public void Synthetic_stays_restricted_and_the_real_profile_selects_openweb()
    {
        var root = FindRepositoryRoot();
        using var appsettings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/AgentCore.Api/appsettings.json")));
        var browser = appsettings.RootElement.GetProperty("Browser");
        Assert.Equal("Restricted", browser.GetProperty("PolicyMode").GetString());
        Assert.True(browser.GetProperty("FixtureEnabled").GetBoolean());

        using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src/AgentCore.Api/Properties/launchSettings.json")));
        var profiles = launch.RootElement.GetProperty("profiles");
        var synthetic = profiles.GetProperty("http").GetProperty("environmentVariables");
        Assert.Equal("Synthetic", synthetic.GetProperty("AgentCore__Profile").GetString());
        Assert.False(synthetic.TryGetProperty("Browser__PolicyMode", out _));

        var real = profiles.GetProperty("http-openrouter").GetProperty("environmentVariables");
        Assert.Equal("Real", real.GetProperty("AgentCore__Profile").GetString());
        Assert.Equal("OpenWeb", real.GetProperty("Browser__PolicyMode").GetString());
        Assert.Equal("false", real.GetProperty("Browser__FixtureEnabled").GetString());
        Assert.Equal("false", real.GetProperty("Browser__Headless").GetString());
        Assert.Equal("InteractiveDemo", real.GetProperty("Browser__InteractionMode").GetString());
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentCore.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
