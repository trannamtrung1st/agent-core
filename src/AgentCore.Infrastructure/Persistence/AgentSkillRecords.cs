namespace AgentCore.Infrastructure.Persistence;
public sealed class AgentDefinitionSkillStateRecord
{
    public string AgentInstanceId { get; set; } = "";
    public string DefinitionSkillId { get; set; } = "";
    public bool Enabled { get; set; }
    public long Revision { get; set; }
    public long UpdatedAtUtc { get; set; }
}
public sealed class AgentInstanceSkillRecord
{
    public string SkillId { get; set; } = "";
    public string AgentInstanceId { get; set; } = "";
    public long Revision { get; set; }
    public long UpdatedAtUtc { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Procedure { get; set; } = "";
    public int Projection { get; set; }
    public bool Enabled { get; set; }
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public long CreatedAtUtc { get; set; }
    public int CreatedBy { get; set; }
    public string? SourceDefinitionId { get; set; }
    public int? SourceDefinitionVersion { get; set; }
    public string? SourceDefinitionSkillId { get; set; }
}
