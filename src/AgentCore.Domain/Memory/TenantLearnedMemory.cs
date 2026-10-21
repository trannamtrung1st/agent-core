namespace AgentCore.Domain.Memory;

public sealed record TenantMemoryReference(Guid TenantId, Guid MemoryId, MemoryScope Scope, Guid? UserPrincipalId,
    Guid? AccountId, Guid? UserId, Guid AgentInstanceId, Guid ExecutorPrincipalId, Guid SessionId, string ContentHash, MemoryItemStatus Status);
// A canonical private learned fact. Source and owner references are actual tenant resources;
// the old global Profile identifier is deliberately absent.
public sealed record TenantLearnedMemory(Guid TenantId, Guid MemoryId, MemoryScope Scope,
    Guid? UserPrincipalId, Guid? AccountId, Guid? UserId, Guid AgentInstanceId, Guid ExecutorPrincipalId,
    Guid SessionId, Guid RunId, Guid RootRunId, Guid? SourceEntryId,
    MemoryKind Kind, MemoryItemStatus Status, string Subject, string Content, string SubjectKey,
    string Source, string ContentHash, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? SupersedesMemoryId = null, string? SourceMemoryIdsJson = null)
{
    public TenantMemoryReference Reference() => new(TenantId, MemoryId, Scope, UserPrincipalId, AccountId, UserId,
        AgentInstanceId, ExecutorPrincipalId, SessionId, ContentHash, Status);
    public string OwnerKey => Scope switch {
        MemoryScope.Session => $"session:{SessionId:D}:{UserPrincipalId ?? ExecutorPrincipalId:D}",
        MemoryScope.IdentityUser when UserPrincipalId is { } user => $"identityUser:{AgentInstanceId:D}:{user:D}",
        MemoryScope.User when UserPrincipalId is { } user => $"user:{user:D}",
        _ => throw new ArgumentException("Cross-Session learned memory requires an actual tenant User.") };
}
