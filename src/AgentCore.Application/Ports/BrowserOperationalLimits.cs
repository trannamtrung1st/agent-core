using AgentCore.Application.Tools;

namespace AgentCore.Application.Ports;

/// <summary>Immutable host budgets. BrowserToolLimits remains the absolute security ceiling.</summary>
public sealed record BrowserOperationalLimits(
    int OperationTimeoutMs = BrowserToolLimits.DefaultOperationTimeoutMs,
    int SnapshotBytes = BrowserToolLimits.MaxSnapshotBytes,
    int CaptureBytes = BrowserToolLimits.MaxCaptureBytes,
    int DownloadBytes = BrowserToolLimits.MaxDownloadBytes,
    int CapturesPerScope = BrowserToolLimits.MaxCapturesPerScope,
    int DownloadsPerScope = BrowserToolLimits.MaxDownloadsPerScope,
    int FindMatches = BrowserToolLimits.MaxFindMatches,
    int WaitTimeoutMs = BrowserToolLimits.MaxObserveTimeoutMs,
    int TextInputLength = BrowserToolLimits.MaxFillLength,
    int AutomaticSettleMs = 1600)
{
    public static BrowserOperationalLimits Default { get; } = new();
    public void Validate()
    {
        if (OperationTimeoutMs is < 100 or > BrowserToolLimits.MaxOperationTimeoutMs || SnapshotBytes is < 256 or > BrowserToolLimits.MaxSnapshotBytes
            || CaptureBytes is < 1024 or > BrowserToolLimits.MaxCaptureBytes || DownloadBytes is < 1024 or > BrowserToolLimits.MaxDownloadBytes
            || CapturesPerScope is < 1 or > BrowserToolLimits.MaxCapturesPerScope || DownloadsPerScope is < 1 or > BrowserToolLimits.MaxDownloadsPerScope
            || FindMatches is < 1 or > BrowserToolLimits.MaxFindMatches || WaitTimeoutMs is < 100 or > BrowserToolLimits.MaxObserveTimeoutMs
            || TextInputLength is < 1 or > BrowserToolLimits.MaxFillLength || AutomaticSettleMs is < 100 or > BrowserToolLimits.MaxObserveTimeoutMs)
            throw new ArgumentException("Browser limits exceed the supported host budgets or safety ceilings.");
    }
}
