using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Experience;

// Canonical owner and provenance. No synthetic/global Profile is attached to a tenant fact.
public sealed record TenantExperience(Guid TenantId, Guid ExperienceId, Guid AgentInstanceId, Guid ExecutorPrincipalId,
    Guid? UserPrincipalId, Guid? AccountId, Guid? UserId, Guid SessionId, Guid RunId, Guid RootRunId,
    ExperienceSourceKind SourceKind, Guid SourceId, long ThroughCursor, string SourceWitnessJson,
    ExperienceContent? Content, string ContentHash, ExperienceVisibility Visibility, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? SourceExperienceIdsJson = null)
{
    public string OwnerKey => UserPrincipalId is { } user ? "user:" + user.ToString("D") : "instance";
    public TenantExperienceReference Reference() => new(TenantId, ExperienceId, AgentInstanceId, ExecutorPrincipalId,
        UserPrincipalId, AccountId, UserId, ContentHash, Visibility, Revision);
    public IReadOnlyList<Guid> Parents => SourceExperienceIdsJson is null ? [] : Array.AsReadOnly(JsonSerializer.Deserialize<Guid[]>(SourceExperienceIdsJson)!);
}

public sealed record TenantExperienceReference(Guid TenantId, Guid ExperienceId, Guid AgentInstanceId, Guid ExecutorPrincipalId,
    Guid? UserPrincipalId, Guid? AccountId, Guid? UserId, string ContentHash, ExperienceVisibility Visibility, long Revision);

// A source read is transient task evidence; a durable Experience fact is a distinct owner.
public sealed record TenantExperienceSourceWitness(ExperienceSourceKind Kind, Guid SourceId, Guid SessionId,
    long SessionRevision, long ThroughCursor, long AfterEntrySequence, int Limit, Guid? SourceRunId,
    long? SourceRunRevision, string ObservationHash,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? PrivateUserPrincipalId = null);
