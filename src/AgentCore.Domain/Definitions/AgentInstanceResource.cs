namespace AgentCore.Domain.Definitions;

public sealed record AgentInstanceResource(Guid InstanceId, Guid ResourceId, string LogicalPath,
    AgentDefinitionResourceKind Kind, string MediaType, string ContentSha256, long ByteLength, bool Enabled,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, SkillAuthor CreatedBy,
    string? SourceDefinitionId = null, int? SourceDefinitionVersion = null, Guid? SourceDefinitionResourceId = null);

public sealed record AgentDefinitionResourceState(Guid InstanceId, Guid ResourceId, bool? EnabledOverride,
    long Revision, DateTimeOffset UpdatedAt);

/// <summary>Origin and immutable content address are execution identity; display paths never shadow another origin.</summary>
public sealed record EffectiveAgentResource(string Key, string LogicalPath, AgentDefinitionResourceKind Kind,
    string MediaType, string ContentSha256, long ByteLength, string VirtualPath);
