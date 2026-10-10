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

    public string ProfileMode { get; set; } = nameof(BrowserProfileMode.EphemeralSession);

    public string ProfileRoot { get; set; } = "data/browser-profiles";

    public string? Channel { get; set; }

    // DomMasking is best-effort. Strict visual confidentiality disables capture entirely.
    public string ScreenshotPrivacy { get; set; } = "DomMasking";

    // Exact host-approved origins whose Canvas/SVG pixels may be captured.
    // Ordinary sensitive DOM masking and whole-frame masking still apply.
    public string[] TrustedVisualCaptureOrigins { get; set; } = [];

    public BrowserOperationalLimits Limits { get; set; } = new();

    public BrowserEnvironmentOptions Environment { get; set; } = new();

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
            ResolvePolicy(),
            ResolveProfile()) { Limits = Limits, ScreenshotAvailable = ScreenshotPrivacy != "Disabled" };

    public BrowserProfileMode ResolveProfile() =>
        string.Equals(ProfileMode, nameof(BrowserProfileMode.PersistentAgent), StringComparison.OrdinalIgnoreCase)
            ? BrowserProfileMode.PersistentAgent
            : BrowserProfileMode.EphemeralSession;

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

/// <summary>Host-controlled context creation settings; never replace an active context implicitly.</summary>
public sealed class BrowserEnvironmentOptions
{
    public string? Device { get; set; }
    public int? ViewportWidth { get; set; }
    public int? ViewportHeight { get; set; }
    public string? Locale { get; set; }
    public string? TimezoneId { get; set; }
    public bool? IsMobile { get; set; }
    public bool? HasTouch { get; set; }
    public float? DeviceScaleFactor { get; set; }
}
