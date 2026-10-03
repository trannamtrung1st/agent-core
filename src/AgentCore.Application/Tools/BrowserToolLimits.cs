namespace AgentCore.Application.Tools;

public static class BrowserToolLimits
{
    public const int MaxUrlLength = 2048;
    public const int MaxRefLength = 128;
    public const int MaxFillLength = 500;
    public const int MaxSelectLength = 200;
    public const int MaxTitleLength = 300;
    public const int MaxVisibleTextLength = 8000;
    public const int MaxElements = 40;
    public const int MaxAccessibleNameLength = 200;
    public const int MaxRoleLength = 80;
    public const int MinObserveTimeoutMs = 100;
    public const int MaxObserveTimeoutMs = 5000;
    public const int DefaultObserveTimeoutMs = 2500;

    public static readonly string[] Operations = ["click", "fill", "select", "press", "check", "uncheck", "upload"];

    public static readonly string[] ObserveWaitModes = ["stable"];

    public static readonly string[] PressKeys = ["Enter", "Tab", "Escape"];
}
