namespace AgentCore.Contracts.Http;

public sealed record AgentInstanceSkillResponse(
    string Key, string Origin, string Name, string Description, string Procedure,
    string Projection, bool Enabled, IReadOnlyList<string> RequiredCapabilities, long Revision,
    int? DefinitionVersion, string? SourceDefinitionId, int? SourceDefinitionVersion, string? SourceDefinitionSkillId,
    IReadOnlyList<string> MissingCapabilities, string? CreatedBy, DateTimeOffset? CreatedAt, DateTimeOffset? UpdatedAt, bool? EnabledOverride = null);

public sealed record DefinitionSkillMutationResponse(string Key, bool Enabled, long Revision);
public sealed record AgentInstanceSkillCustomizeResponse(AgentInstanceSkillResponse InstanceSkill, DefinitionSkillMutationResponse DefinitionSkill);
