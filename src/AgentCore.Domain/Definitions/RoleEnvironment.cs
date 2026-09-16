namespace AgentCore.Domain.Definitions;

public sealed record RoleEnvironment(
    IReadOnlyList<string>? Harness = null,
    IReadOnlyList<KnowledgeSourceRef>? KnowledgeSources = null,
    IReadOnlyList<string>? ToolAllowlist = null,
    WorkspaceTemplatePolicy? Workspace = null,
    AttachmentStorePolicy? Attachments = null)
{
    public static RoleEnvironment Empty { get; } = new();

    public IReadOnlyList<string> HarnessList => Harness ?? [];

    public IReadOnlyList<KnowledgeSourceRef> KnowledgeList => KnowledgeSources ?? [];

    public IReadOnlyList<string> ToolList => ToolAllowlist ?? [];

    public WorkspaceTemplatePolicy WorkspacePolicy => Workspace ?? new WorkspaceTemplatePolicy(null);

    public AttachmentStorePolicy AttachmentPolicy => Attachments ?? new AttachmentStorePolicy(false);
}

public sealed record KnowledgeSourceRef(string Identity, string Title, string Citation);

public sealed record WorkspaceTemplatePolicy(string? TemplateId = null);

public sealed record AttachmentStorePolicy(bool AllowUnreadUnsupportedTypes = false);

public static class RoleEnvironments
{
    public static RoleEnvironment Of(AgentDefinition definition) =>
        definition.Environment ?? RoleEnvironment.Empty;
}
