using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class ToolResources
{
    public static bool IsOccurrence(TriggerKind kind) =>
        kind is TriggerKind.ScheduledOccurrence or TriggerKind.ApplicationEvent or TriggerKind.ManualInvocation;

    public static bool IsSessionTool(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) && descriptor.Scope == ToolResourceScope.Session;

    public static bool IsTriggerWrite(string toolName) =>
        toolName is ToolCatalog.AutomationCreate
            or ToolCatalog.AutomationUpdate
            or ToolCatalog.AutomationDelete or ToolCatalog.AutomationRun or ToolCatalog.AutomationDisable;
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
    bool SupportsVision = false,
    string? CaptureScope = null,
    Guid? AgentRunId = null,
    HarnessChatContext? Harness = null,
    IReadOnlyList<HarnessSourceReceipt>? HarnessSources = null,
    string? OwnerTurnText = null,
    bool SupportsTools = true,
    AgentCore.Domain.Conversation.AgentRunModelPin? Model = null,
    string? WorkspaceCwd = null, Guid? OwnedSessionId = null);

public sealed record ToolDescriptor(
    ModelToolDefinition ModelDefinition,
    ToolEffect Effect,
    ToolOfferRule OfferRule = ToolOfferRule.RoleAllowlist,
    ToolResourceScope Scope = ToolResourceScope.Owner,
    ToolReplaySafety ReplaySafety = ToolReplaySafety.ReplaySafe)
{
    public string Name => ModelDefinition.Name;
    public string Category => Name.Split('.')[0];
    public string Summary => ModelDefinition.Description.Length <= 240 ? ModelDefinition.Description : ModelDefinition.Description[..240];
    public IReadOnlyList<string> Tags => Category switch { "browser" => ["website", "navigate", "interact"], "email" => ["mail", "messages", "draft", "send"], "automation" => ["schedule", "event", "reminder"], _ => [Category] };
    public bool DefinitionAuthorizable => OfferRule is ToolOfferRule.RoleAllowlist
        or ToolOfferRule.ConfigurationWhenRoleAllows or ToolOfferRule.SessionAttachmentsWhenRoleAllows;
    public bool Discoverable => OfferRule is ToolOfferRule.RoleAllowlist or ToolOfferRule.ConfigurationWhenRoleAllows;
    public string DefaultProjectionClass => Name is ToolCatalog.CapabilitiesLoad or ToolCatalog.SkillsLoad ? "bootstrap" : Discoverable ? "onDemand" : "contextOnly";
}
