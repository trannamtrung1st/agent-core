namespace AgentCore.Infrastructure.Persistence;

public sealed class AdminEventRecord
{
    public string EventId { get; set; } = "";
    public string OperationId { get; set; } = "";
    public long OccurredAtUtc { get; set; }
    public string ActorKind { get; set; } = "";
    public string Operation { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public long? Revision { get; set; }
    public int? Version { get; set; }
    public string SummaryJson { get; set; } = "{}";
}
