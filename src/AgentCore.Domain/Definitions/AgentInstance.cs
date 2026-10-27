namespace AgentCore.Domain.Definitions;

public enum AgentInstanceLifecycle
{
    Active,
    Archived
}

public sealed record AgentInstance(
    Guid InstanceId,
    string DefinitionId,
    int ActiveVersion,
    AgentIdentity Persona,
    AgentInstanceLifecycle Lifecycle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Revision = 1,
    long PersonaRevision = 1,
    AgentCore.Domain.Models.ModelPreference? UnattendedModel = null,
    HarnessManagementState? HarnessManagement = null,
    ExecutionBudgetPolicy? ExecutionBudgets = null, InstanceSettingsOverrides? SettingsOverrides = null,
    [property: System.Text.Json.Serialization.JsonIgnore] AgentBrowserProfileReset? BrowserProfileReset = null, AgentCore.Domain.Tasks.AgentCooperationPolicy? Cooperation = null);
