using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolCatalog
{
    public const string KnowledgeRetrieve = "knowledge.retrieve";
    public const string AttachmentsRead = "attachments.read";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceList = "workspace.list";
    public const string WorkspaceWrite = "workspace.write";
    public const string WorkspacePatch = "workspace.patch";
    public const string WorkspaceSearch = "workspace.search";
    public const string WorkspaceMove = "workspace.move";
    public const string ArtifactsCreate = "artifacts.create";
    public const string ArtifactsCreateFromWorkspace = "artifacts.create_from_workspace";
    public const string ArtifactsVerify = "artifacts.verify";
    public const string SandboxRun = "sandbox.run";
    public const string WebSearch = "web.search";
    public const string WebFetch = "web.fetch";
    public const string HttpRequest = "http.request";
    public const string EmailSearch = "email.search";
    public const string EmailRead = "email.read";
    public const string EmailCreateDraft = "email.create_draft";
    public const string EmailSend = "email.send";
    public const string DemoSensitiveAction = "demo.sensitive_action";
    public const string TriggerScheduleOnce = "trigger.schedule_once";
    public const string TriggerScheduleRecurring = "trigger.schedule_recurring";
    public const string TriggerList = "trigger.list";
    public const string TriggerUpdate = "trigger.update";
    public const string TriggerCancel = "trigger.cancel";

    public static IReadOnlyList<ModelToolDefinition> For(
        AgentDefinition definition,
        AgentContext? context,
        IToolConfigurationGate configurationGate)
    {
        if (context is not null && !context.ModelSupportsTools)
        {
            return [];
        }

        if (context?.Trigger.Kind == TriggerKind.ScheduledOccurrence)
        {
            return [];
        }

        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(name, out var descriptor)
                || descriptor.OfferRule is not (ToolOfferRule.RoleAllowlist or ToolOfferRule.ConfigurationWhenRoleAllows)
                || !ToolPolicy.IsOffered(descriptor, definition, context, configurationGate)
                || !seen.Add(name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        if (ToolRegistry.TryGet(AttachmentsRead, out var attachmentDescriptor)
            && attachmentDescriptor.OfferRule == ToolOfferRule.SessionAttachmentsWhenRoleAllows
            && ToolPolicy.IsOffered(attachmentDescriptor, definition, context, configurationGate)
            && seen.Add(AttachmentsRead))
        {
            offered.Add(attachmentDescriptor.ModelDefinition);
        }

        return offered;
    }

    public static bool OffersAttachmentRead(
        AgentDefinition definition,
        AgentContext context,
        IToolConfigurationGate configurationGate) =>
        ToolPolicy.IsOffered(definition, context, AttachmentsRead, configurationGate);

    public static bool SessionHasAttachments(AgentContext context) =>
        context.SessionAttachments is { Count: > 0 }
        || context.AttachmentContents is { Count: > 0 };

    public static bool IsPermittedForExecution(AgentDefinition definition, string toolName) =>
        RolePermissions.AllowsTool(definition, toolName);

    public static IEnumerable<string> AllKnownNames() => ToolRegistry.AllKnownNames();

    public static ToolEffect EffectOf(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) ? descriptor.Effect : throw new KeyNotFoundException($"Unknown tool '{toolName}'.");
}
