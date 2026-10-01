using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Browser;

public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    public bool Enabled { get; set; }

    public bool Headless { get; set; }

    public string InteractionMode { get; set; } = "InteractiveDemo";

    public int FixturePort { get; set; } = 5091;

    public string[] TargetOrigins { get; set; } = ["http://127.0.0.1:5091"];

    public BrowserHostPolicy ToHostPolicy() =>
        new(
            Enabled,
            Headless,
            string.Equals(InteractionMode, nameof(BrowserInteractionMode.InteractiveDemo), StringComparison.Ordinal)
                ? BrowserInteractionMode.InteractiveDemo
                : BrowserInteractionMode.ReadNavigation,
            TargetOrigins ?? []);
}
