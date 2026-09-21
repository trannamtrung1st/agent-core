namespace AgentCore.Application.Tools;

public static class ToolApprovalLimits
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxSummaryLength = 120;
    public const int MaxDetailKeyCount = 8;
    public const int MaxDetailValueLength = 200;
}
