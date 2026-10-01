using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Browser;

public sealed class BrowserOptions
{
    public const string SectionName = "Browser";

    public bool Enabled { get; set; }

    public bool Headless { get; set; }

    public string InteractionMode { get; set; } = "InteractiveDemo";

    public string PolicyMode { get; set; } = nameof(BrowserPolicyMode.Restricted);

    public bool FixtureEnabled { get; set; } = true;

    public int FixturePort { get; set; } = 5091;

    public string[] NavigationOrigins { get; set; } = ["http://127.0.0.1:5091"];

    public string[]? InteractionOrigins { get; set; }

    public string[] ResourceOrigins { get; set; } = [];

    public string[]? TargetOrigins { get; set; }

    public BrowserHostPolicy ToHostPolicy() =>
        new(
            Enabled,
            Headless,
            ResolveMode(),
            ResolveNavigation(),
            ResolveInteraction(ResolveNavigation()),
            ResourceOrigins ?? [],
            ResolvePolicy());

    public BrowserPolicyMode ResolvePolicy() =>
        string.Equals(PolicyMode, nameof(BrowserPolicyMode.OpenWeb), StringComparison.OrdinalIgnoreCase)
            ? BrowserPolicyMode.OpenWeb
            : BrowserPolicyMode.Restricted;

    public BrowserInteractionMode ResolveMode() =>
        string.Equals(InteractionMode, nameof(BrowserInteractionMode.InteractiveDemo), StringComparison.Ordinal)
            ? BrowserInteractionMode.InteractiveDemo
            : BrowserInteractionMode.ReadNavigation;

    public string[] ResolveNavigation() =>
        TargetOrigins is { Length: > 0 } ? TargetOrigins : NavigationOrigins ?? [];

    public IReadOnlyList<string> ResolveInteraction(IReadOnlyList<string> navigation) =>
        InteractionOrigins ?? navigation.Where(BrowserTargetPolicy.IsLoopback).ToArray();
}
