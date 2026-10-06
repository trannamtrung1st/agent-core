namespace AgentCore.Infrastructure.Persistence;

public sealed class ExperienceRecord
{
    public string ExperienceId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public string ProfileId { get; set; } = "";
    public int SourceKind { get; set; }
    public string SourceId { get; set; } = "";
    public long ThroughCursor { get; set; }
    public long CreatedAtUtc { get; set; }
    public string PayloadJson { get; set; } = "";
    public long Revision { get; set; }
}
public sealed class ExperienceSettingsRecord
{
    public string AgentInstanceId { get; set; } = "";
    public bool Enabled { get; set; }
    public long Revision { get; set; }
}

public sealed class IdentityMaintenanceSettingsRecord
{
    public string AgentInstanceId { get; set; } = "";
    public bool AllowAgentConsolidation { get; set; }
    public long Revision { get; set; }
}

public sealed class ContinuityMaintenanceSettingsRecord
{
    public string AgentInstanceId { get; set; } = "";
    public int? IntervalSeconds { get; set; }
    public long Revision { get; set; }
    public long? LastMaintenanceAtUtc { get; set; }
}
