namespace AgentCore.Application.Tools;

public static class BrowserToolLimits
{
    public const int MaxUrlLength = 2048;
    public const int MaxRefLength = 128;
    public const int MaxFillLength = 500;
    public const int MaxSelectLength = 200;
    public const int MaxTitleLength = 300;
    public const int MaxVisibleTextLength = 8000;
    public const int MaxSnapshotChars = 8000;
    public const int MaxFindMatches = 20;
    public const int MaxAccessibleNameLength = 200;
    public const int MaxRoleLength = 80;
    public const int MinObserveTimeoutMs = 100;
    public const int MaxObserveTimeoutMs = 5000;
    public const int DefaultObserveTimeoutMs = 2500;
    public const int MaxCaptureBytes = 1_500_000;
    public const int MaxCapturesPerScope = 4;
    public const int MaxWorkCaptures = 8;
    public const int MaxDownloadBytes = 5 * 1024 * 1024;
    public const int MaxDownloadsPerScope = 2;
    public const int MaxCaptureWidth = 1280;
    public const int MaxCaptureHeight = 800;
    public const int DefaultScrollDelta = 400;
    public const int MaxScrollDelta = 2000;

    public static readonly string[] Operations =
    [
        "click", "fill_credential", "fill", "select", "press", "check", "uncheck", "upload",
        "doubleClick", "hover", "scroll", "drag"
    ];

    public static readonly string[] ObserveWaitModes = ["stable", "navigation", "role"];

    public static readonly string[] NavigateOperations = ["goto", "back", "forward", "reload"];


    public static readonly string[] ScrollDirections = ["up", "down", "left", "right"];

}
