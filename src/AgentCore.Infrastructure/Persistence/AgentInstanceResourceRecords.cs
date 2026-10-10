namespace AgentCore.Infrastructure.Persistence;

public sealed class AgentInstanceResourceRecord
{
    public string InstanceId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string LogicalPath { get; set; } = "";
    public long Revision { get; set; }
    public string PayloadJson { get; set; } = "";
}
public sealed class AgentDefinitionResourceStateRecord
{
    public string InstanceId { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public bool? EnabledOverride { get; set; }
    public long Revision { get; set; }
    public long UpdatedAtUtc { get; set; }
}
