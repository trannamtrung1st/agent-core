namespace AgentCore.Domain.Definitions;

public enum AgentDefinitionResourceKind
{
    Knowledge = 1,
    Reference = 2,
    Template = 3,
    StaticAsset = 4,
    EvalFixture = 5
}

public static class AgentResourceLimits
{
    public const long MaxItemBytes = 8 * 1024 * 1024;
    public const int MaxItemsPerDraft = 64;
    public const long MaxAggregateBytes = 64 * 1024 * 1024;
}

public sealed record AgentDefinitionDraftResource(
    Guid ResourceId,
    Guid DraftId,
    string LogicalPath,
    AgentDefinitionResourceKind Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength,
    DateTimeOffset UpdatedAt);

public sealed record AgentDefinitionPublicationResource(
    string DefinitionId,
    int Version,
    Guid ResourceId,
    string LogicalPath,
    AgentDefinitionResourceKind Kind,
    string MediaType,
    string ContentSha256,
    long ByteLength);
