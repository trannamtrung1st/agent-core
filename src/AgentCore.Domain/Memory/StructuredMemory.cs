namespace AgentCore.Domain.Memory;

public enum MemoryKind
{
    Fact = 0,
    Preference = 1,
    Goal = 2,
    Decision = 3,
    OpenLoop = 4
}

public enum MemoryItemStatus
{
    Active = 0,
    Superseded = 1,
    Deleted = 2
}

public enum MemoryScope
{
    Session = 0,
    IdentityUser = 1,
    User = 2
}

public sealed record MemoryProvenance(
    string Source,
    IReadOnlyList<Guid> SourceEntryIds,
    Guid? SupersedesMemoryId,
    DateTimeOffset RecordedAt,
    Guid? OriginMemoryId = null,
    Guid? OriginSessionId = null);

public sealed record StructuredMemoryItem(
    Guid MemoryId,
    Guid SessionId,
    MemoryKind Kind,
    MemoryItemStatus Status,
    string Subject,
    string Content,
    string SubjectKey,
    MemoryProvenance Provenance,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    MemoryScope Scope = MemoryScope.Session,
    Guid? OwnerInstanceId = null,
    Guid? OwnerProfileId = null)
{
    public static string CollapseSubject(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    public static string SubjectKeyFor(string collapsedSubject) =>
        collapsedSubject.ToLowerInvariant();
}
