namespace AgentCore.Domain.Experience;

/// <summary>LastMaintenanceAtUtc records an eligible evaluation claim, not model completion.</summary>
public sealed record ContinuityMaintenanceSettings(Guid AgentInstanceId, int? IntervalSeconds,
    long Revision, DateTimeOffset? LastMaintenanceAtUtc);
