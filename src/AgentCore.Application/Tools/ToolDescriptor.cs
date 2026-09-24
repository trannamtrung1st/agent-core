using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class ToolResources
{
    public static bool IsOccurrence(TriggerKind kind) =>
        kind is TriggerKind.ScheduledOccurrence or TriggerKind.ApplicationEvent;

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

public sealed record ToolExecutionAdmission(bool Detached, TriggerKind TriggerKind);

public sealed record ToolDescriptor(
    ModelToolDefinition ModelDefinition,
    ToolEffect Effect,
    ToolOfferRule OfferRule = ToolOfferRule.RoleAllowlist,
    ToolResourceScope Scope = ToolResourceScope.Owner,
    ToolReplaySafety ReplaySafety = ToolReplaySafety.ReplaySafe)
{
    public string Name => ModelDefinition.Name;
}
