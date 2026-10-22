using System.Text.Json;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// Exact local Instance plan; the existing approval/effect owner commits it once.
public sealed record PreparedManagementMutation(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    string ConfigurationHash, string BeforeStableHash, string AfterStableHash, string ChangesJson,
    InstanceSettingsOverrides? Settings = null, HarnessManagementState? Management = null,
    IReadOnlyList<InstanceAutomationPolicyChange>? AutomationChanges = null,
    string? UnattendedModelCatalogKey = null, string? UnattendedReasoningEffort = null)
{
    public bool Equals(PreparedManagementMutation? other) => other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(this));
}
