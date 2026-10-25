using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Workspaces;

public static class SpaceWorkspaceCleanupCursor
{
    public static string Of(Guid tenant, Guid space) => tenant.ToString("D") + "/" + space.ToString("D");
    public static void Validate(string? cursor, int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        if (cursor is not null && (cursor.Length != 73 || cursor[36] != '/'
            || !Guid.TryParseExact(cursor[..36], "D", out var tenant) || tenant == Guid.Empty
            || !Guid.TryParseExact(cursor[37..], "D", out var space) || space == Guid.Empty || Of(tenant, space) != cursor))
            throw new ArgumentException("Invalid cleanup cursor.", nameof(cursor));
    }
}
