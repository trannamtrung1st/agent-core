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

    private static readonly IReadOnlyDictionary<string, ModelToolDefinition> Known =
        new Dictionary<string, ModelToolDefinition>(StringComparer.Ordinal)
        {
            [KnowledgeRetrieve] = new(
                KnowledgeRetrieve,
                "Retrieve an approved knowledge identity for this role.",
                """{"type":"object","properties":{"identity":{"type":"string"}},"required":["identity"]}"""),
            [AttachmentsRead] = new(
                AttachmentsRead,
                "Read a session attachment by AttachmentId. Do not pass host filesystem paths.",
                """{"type":"object","properties":{"attachmentId":{"type":"string"}},"required":["attachmentId"]}"""),
            [WorkspaceRead] = new(
                WorkspaceRead,
                "Read a logical execution-view path under /agent, /attachments, or /workspace.",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
            [WorkspaceWrite] = new(
                WorkspaceWrite,
                "Write a UTF-8 file under /workspace only.",
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}"""),
            [ArtifactsCreate] = new(
                ArtifactsCreate,
                "Create a session-owned artifact from UTF-8 content.",
                """{"type":"object","properties":{"displayName":{"type":"string"},"contentType":{"type":"string"},"content":{"type":"string"}},"required":["displayName","content"]}"""),
            [ArtifactsVerify] = new(
                ArtifactsVerify,
                "Verify a session-owned artifact id.",
                """{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}"""),
            [SandboxRun] = new(
                SandboxRun,
                "Run a least-privilege sandbox command (echo, true, cat of /workspace/working files). Not a host process or shell.",
                """{"type":"object","properties":{"verb":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"exportPath":{"type":"string"}},"required":["verb"]}""")
        };

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
            if (Known.TryGetValue(name, out var tool) && RolePermissions.AllowsTool(definition, name) && seen.Add(name))
            {
                offered.Add(tool);
            }
        }

        if (context is not null
            && SessionHasAttachments(context)
            && Known.TryGetValue(AttachmentsRead, out var attachmentsRead)
            && RolePermissions.AllowsTool(definition, AttachmentsRead)
            && seen.Add(AttachmentsRead))
        {
            offered.Add(attachmentsRead);
        }

        return offered;
    }

    public static bool OffersAttachmentRead(AgentDefinition definition, AgentContext context) =>
        For(definition, context).Any(item => string.Equals(item.Name, AttachmentsRead, StringComparison.Ordinal));

    public static bool SessionHasAttachments(AgentContext context) =>
        context.SessionAttachments is { Count: > 0 }
        || context.AttachmentContents is { Count: > 0 };

    public static bool IsPermittedForExecution(AgentDefinition definition, string toolName) =>
        RolePermissions.AllowsTool(definition, toolName);

    public static IEnumerable<string> AllKnownNames() => Known.Keys;
}
