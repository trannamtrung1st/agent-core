using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public static class ToolCatalog
{
    public const string KnowledgeRetrieve = "knowledge.retrieve";
    public const string AttachmentsRead = "attachments.read";
    public const string WorkspaceRead = "workspace.read";
    public const string WorkspaceWrite = "workspace.write";
    public const string ArtifactsCreate = "artifacts.create";
    public const string ArtifactsVerify = "artifacts.verify";
    public const string SandboxRun = "sandbox.run";

    public static IReadOnlyList<ModelToolDefinition> For(AgentDefinition definition, AgentContext? context = null)
    {
        if (context is not null && !context.ModelSupportsTools)
        {
            return [];
        }

        var offered = new List<ModelToolDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in RoleEnvironments.Of(definition).ToolList)
        {
            if (!ToolRegistry.TryGet(name, out var descriptor)
                || descriptor.OfferRule != ToolOfferRule.RoleAllowlist
                || !ToolPolicy.IsOffered(descriptor, definition, context)
                || !seen.Add(name))
            {
                continue;
            }

            offered.Add(descriptor.ModelDefinition);
        }

        if (ToolRegistry.TryGet(AttachmentsRead, out var attachmentDescriptor)
            && attachmentDescriptor.OfferRule == ToolOfferRule.SessionAttachmentsWhenRoleAllows
            && ToolPolicy.IsOffered(attachmentDescriptor, definition, context)
            && seen.Add(AttachmentsRead))
        {
            offered.Add(attachmentDescriptor.ModelDefinition);
        }

        return offered;
    }

    public static bool OffersAttachmentRead(AgentDefinition definition, AgentContext context) =>
        ToolPolicy.IsOffered(definition, context, AttachmentsRead);

    public static bool SessionHasAttachments(AgentContext context) =>
        context.SessionAttachments is { Count: > 0 }
        || context.AttachmentContents is { Count: > 0 };

    public static bool IsPermittedForExecution(AgentDefinition definition, string toolName) =>
        RolePermissions.AllowsTool(definition, toolName);

    public static IEnumerable<string> AllKnownNames() => ToolRegistry.AllKnownNames();

    public static ToolEffect EffectOf(string toolName) =>
        ToolRegistry.TryGet(toolName, out var descriptor) ? descriptor.Effect : throw new KeyNotFoundException($"Unknown tool '{toolName}'.");
}
