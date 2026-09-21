using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class ToolRegistry
{
    private static readonly IReadOnlyDictionary<string, ToolDescriptor> Registered =
        new Dictionary<string, ToolDescriptor>(StringComparer.Ordinal)
        {
            [ToolCatalog.KnowledgeRetrieve] = Descriptor(
                ToolCatalog.KnowledgeRetrieve,
                "Retrieve an approved knowledge identity for this role.",
                """{"type":"object","properties":{"identity":{"type":"string"}},"required":["identity"]}""",
                ToolEffect.ReadOnly),
            [ToolCatalog.AttachmentsRead] = Descriptor(
                ToolCatalog.AttachmentsRead,
                "Read a session attachment by AttachmentId. Do not pass host filesystem paths.",
                """{"type":"object","properties":{"attachmentId":{"type":"string"}},"required":["attachmentId"]}""",
                ToolEffect.ReadOnly,
                ToolOfferRule.SessionAttachmentsWhenRoleAllows),
            [ToolCatalog.WorkspaceRead] = Descriptor(
                ToolCatalog.WorkspaceRead,
                "Read a logical execution-view path under /agent, /attachments, or /workspace.",
                """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
                ToolEffect.ReadOnly),
            [ToolCatalog.WorkspaceWrite] = Descriptor(
                ToolCatalog.WorkspaceWrite,
                "Write a UTF-8 file under /workspace only.",
                """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""",
                ToolEffect.Write),
            [ToolCatalog.ArtifactsCreate] = Descriptor(
                ToolCatalog.ArtifactsCreate,
                "Create a session-owned artifact from UTF-8 content.",
                """{"type":"object","properties":{"displayName":{"type":"string"},"contentType":{"type":"string"},"content":{"type":"string"}},"required":["displayName","content"]}""",
                ToolEffect.Write),
            [ToolCatalog.ArtifactsVerify] = Descriptor(
                ToolCatalog.ArtifactsVerify,
                "Verify a session-owned artifact id.",
                """{"type":"object","properties":{"artifactId":{"type":"string"}},"required":["artifactId"]}""",
                ToolEffect.ReadOnly),
            [ToolCatalog.SandboxRun] = Descriptor(
                ToolCatalog.SandboxRun,
                "Run a least-privilege sandbox command (echo, true, cat of /workspace/working files). Not a host process or shell.",
                """{"type":"object","properties":{"verb":{"type":"string"},"arguments":{"type":"array","items":{"type":"string"}},"exportPath":{"type":"string"}},"required":["verb"]}""",
                ToolEffect.Write)
        };

    public static IEnumerable<ToolDescriptor> All => Registered.Values;

    public static bool TryGet(string toolName, out ToolDescriptor descriptor) =>
        Registered.TryGetValue(toolName, out descriptor!);

    public static ToolDescriptor Get(string toolName) =>
        Registered.TryGetValue(toolName, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Unknown tool '{toolName}'.");

    public static IEnumerable<string> AllKnownNames() => Registered.Keys;

    private static ToolDescriptor Descriptor(
        string name,
        string description,
        string parametersJson,
        ToolEffect effect,
        ToolOfferRule offerRule = ToolOfferRule.RoleAllowlist) =>
        new(new ModelToolDefinition(name, description, parametersJson), effect, offerRule);
}
