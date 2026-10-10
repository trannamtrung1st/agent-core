using AgentCore.Application.Admin;

namespace AgentCore.Application.Ports;

public enum BrowserScreenshotPrivacyMode { Protected, Unmasked, Disabled }

/// <summary>Immutable capture policy. Origin grants never authorize navigation or interaction.</summary>
public sealed record BrowserScreenshotPolicy(
    BrowserScreenshotPrivacyMode Mode,
    IReadOnlyList<string> UnmaskedOrigins,
    IReadOnlyList<string> TrustedGraphicsOrigins,
    long Revision = 0)
{
    public static BrowserScreenshotPolicy Protected { get; } = new(BrowserScreenshotPrivacyMode.Protected, [], []);
    public bool IsUnmasked(string url) => Mode == BrowserScreenshotPrivacyMode.Unmasked && Matches(UnmaskedOrigins, url);
    public bool TrustsGraphics(string url) => Matches(TrustedGraphicsOrigins, url);
    private static bool Matches(IReadOnlyList<string> origins, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
        && origins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase);
}

public sealed record BrowserPrivacyAuthority(bool CaptureAllowed, bool UnmaskedAllowed,
    IReadOnlyList<string> UnmaskedOriginCeiling, IReadOnlyList<string> GraphicsOriginCeiling);

public interface IBrowserPrivacyStore
{
    bool IsDurable { get; }
    ValueTask<BrowserScreenshotPolicy?> ReadAsync(CancellationToken ct = default);
    /// <summary>CAS plus Admin history in one commit; initial expected revision is zero.</summary>
    ValueTask SaveAsync(BrowserScreenshotPolicy policy, long expectedRevision, AdminEventAppend audit, CancellationToken ct = default);
}

/// <summary>Host retirement also cancels unpublished capture/artifact work.</summary>
public interface IBrowserCaptureLifetime { CancellationToken CaptureLifetime { get; } }
