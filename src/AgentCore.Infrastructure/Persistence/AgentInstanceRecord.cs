namespace AgentCore.Infrastructure.Persistence;

public sealed class AgentInstanceRecord
{
    public string InstanceId { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public int ActiveVersion { get; set; }
    public string PersonaJson { get; set; } = "";
    public string Lifecycle { get; set; } = "";
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public bool Compatibility { get; set; }
}
