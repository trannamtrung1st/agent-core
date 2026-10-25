using System.Text.Json.Serialization;

namespace AgentCore.Domain.Definitions;

public sealed record DefinitionExecutionPolicy(
    IReadOnlyList<string>? Harness = null,
    IReadOnlyList<KnowledgeSourceRef>? KnowledgeSources = null,
    IReadOnlyList<string>? ToolAllowlist = null,
    WorkspaceTemplatePolicy? Workspace = null,
    AttachmentStorePolicy? Attachments = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CapabilityAuthorization? Capabilities = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CapabilityProjectionPolicy? Projection = null)
{
    public static DefinitionExecutionPolicy Empty { get; } = new();

    public IReadOnlyList<string> HarnessList => Harness ?? [];

    public IReadOnlyList<KnowledgeSourceRef> KnowledgeList => KnowledgeSources ?? [];

    public IReadOnlyList<string> ToolList => Capabilities?.ResolvedCapabilities ?? ToolAllowlist ?? [];

    public WorkspaceTemplatePolicy WorkspacePolicy => Workspace ?? new WorkspaceTemplatePolicy(null);

    public AttachmentStorePolicy AttachmentPolicy => Attachments ?? new AttachmentStorePolicy(false);
}

public sealed record KnowledgeSourceRef(
    string Identity,
    string Title,
    string Citation,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ResourcePath = null);

public sealed record WorkspaceTemplatePolicy(string? TemplateId = null);

public sealed record AttachmentStorePolicy(bool AllowUnreadUnsupportedTypes = false);

public static class DefinitionExecutionPolicies
{
    public static DefinitionExecutionPolicy Of(AgentDefinition definition) =>
        definition.ExecutionPolicy ?? DefinitionExecutionPolicy.Empty;
}

public sealed record CapabilityAuthorization(string Mode, IReadOnlyList<string> ResolvedCapabilities, string? AuthorizationFingerprint = null);
public sealed record CapabilityProjectionPolicy(IReadOnlyList<string> AlwaysCapabilities);
