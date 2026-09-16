namespace AgentCore.Application.Tools;

public static class ToolLimits
{
    public const int MaxSteps = 12;
    public static readonly TimeSpan PerTool = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Overall = TimeSpan.FromSeconds(120);
    public const int MaxOutputBytes = 8 * 1024 * 1024;
}
