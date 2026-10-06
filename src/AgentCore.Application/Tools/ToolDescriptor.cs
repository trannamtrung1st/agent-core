using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class ToolResources
{
    public static bool IsOccurrence(TriggerKind kind) =>
        kind is TriggerKind.ScheduledOccurrence or TriggerKind.ApplicationEvent or TriggerKind.ThoughtActivation;

    public static bool IsSessionTool(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) && descriptor.Scope == ToolResourceScope.Session;

    public static bool IsTriggerWrite(string toolName) =>
        toolName is ToolCatalog.TriggerScheduleOnce
            or ToolCatalog.TriggerScheduleRecurring
            or ToolCatalog.TriggerUpdate
            or ToolCatalog.TriggerCancel;
}

public enum ToolResourceScope
{
    Session,
    Owner,
    External
}

public sealed record ToolExecutionAdmission(
    bool Detached,
    TriggerKind TriggerKind,
    bool IntermediateMessagingAllowed = false,
    Guid? AgentInstanceId = null,
    bool TrustedConnection = false,
    bool SupportsVision = false,
    string? CaptureScope = null,
    Guid? WorkItemId = null,
    HarnessChatContext? Harness = null,
    IReadOnlyList<HarnessSourceReceipt>? HarnessSources = null,
    string? OwnerTurnText = null,
    bool SupportsTools = true,
    AgentCore.Domain.Work.WorkModelPin? Model = null,
    string? WorkspaceCwd = null);

public sealed record ToolDescriptor(
    ModelToolDefinition ModelDefinition,
    ToolEffect Effect,
    ToolOfferRule OfferRule = ToolOfferRule.RoleAllowlist,
    ToolResourceScope Scope = ToolResourceScope.Owner,
    ToolReplaySafety ReplaySafety = ToolReplaySafety.ReplaySafe)
{
    public string Name => ModelDefinition.Name;
    public string Category => Name.StartsWith("trigger.", StringComparison.Ordinal) ? "scheduling" : Name.Split('.')[0];
    public string Summary => ModelDefinition.Description.Length <= 240 ? ModelDefinition.Description : ModelDefinition.Description[..240];
    public IReadOnlyList<string> Tags => Category switch { "browser" => ["website", "navigate", "interact"], "email" => ["mail", "messages", "draft", "send"], "scheduling" => ["schedule", "reminder"], _ => [Category] };
    public bool Discoverable => OfferRule is ToolOfferRule.RoleAllowlist or ToolOfferRule.ConfigurationWhenRoleAllows;
    public string DefaultProjectionClass => Name is ToolCatalog.CapabilitiesLoad or ToolCatalog.SkillsLoad ? "bootstrap" : Discoverable ? "onDemand" : "contextOnly";
}
