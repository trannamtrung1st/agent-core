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
    public long Revision { get; set; } = 1;
    public long PersonaRevision { get; set; } = 1;
    public string? UnattendedModelCatalogKey { get; set; }
    public string? UnattendedReasoningEffort { get; set; }
    public string? SettingsOverridesJson { get; set; }
    public string? ExecutionBudgetsJson { get; set; }
    public string? HarnessManagementJson { get; set; }
}
