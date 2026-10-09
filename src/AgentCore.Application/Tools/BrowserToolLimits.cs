namespace AgentCore.Application.Tools;

public static class BrowserToolLimits
{
    public const int MaxUrlLength = 2048;
    public const int MaxFillLength = 500;
    public const int MaxTitleLength = 300;
    public const int MaxSnapshotBytes = 8000;
    public const int MaxFindMatches = 20;
    public const int MaxAccessibleNameLength = 200;
    public const int MinObserveTimeoutMs = 100;
    public const int MaxObserveTimeoutMs = 5000;
    public const int DefaultObserveTimeoutMs = 2500;
    public const int MaxCaptureBytes = 1_500_000;
    public const int MaxCapturesPerScope = 4;
    public const int MaxDownloadBytes = 5 * 1024 * 1024;
    public const int MaxDownloadsPerScope = 2;
    public const int MaxCaptureWidth = 1280;
    public const int MaxCaptureHeight = 800;

    public static readonly string[] TargetActions =
    [
        "click", "fill_credential", "fill", "select", "press", "check", "uncheck", "upload",
        "doubleClick", "hover", "scroll", "drag"
    ];





}
