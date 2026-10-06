using AgentCore.Application.Continuity;

namespace AgentCore.Api;

public sealed class ContinuityMaintenanceOptions
{
    public const string SectionName = "ContinuityMaintenance";
    public int PollIntervalSeconds { get; set; } = 60;
    public int MinimumIntervalSeconds { get; set; } = 60;
    public int DefaultIntervalSeconds { get; set; } = 300;
    public int MaximumIntervalSeconds { get; set; } = 86400;
    public ContinuityMaintenancePolicy ToPolicy() => new(PollIntervalSeconds, MinimumIntervalSeconds, DefaultIntervalSeconds, MaximumIntervalSeconds);
}
