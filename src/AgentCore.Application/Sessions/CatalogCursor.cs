using System.Globalization;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public static class CatalogCursor
{
    public static string Encode(DateTimeOffset updatedAt, Guid sessionId)
    {
        var payload = $"{updatedAt.ToUnixTimeMilliseconds().ToString("D20", CultureInfo.InvariantCulture)}:{sessionId:D}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static (long UpdatedAtMs, Guid SessionId) Decode(string cursor)
    {
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var payload = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = payload.Split(':', 2, StringSplitOptions.None);
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                || !Guid.TryParse(parts[1], out var id))
            {
                throw AgentCoreErrors.Validation("Catalog cursor is invalid.");
            }

            return (ms, id);
        }
        catch (FormatException)
        {
            throw AgentCoreErrors.Validation("Catalog cursor is invalid.");
        }
        catch (ArgumentException)
        {
            throw AgentCoreErrors.Validation("Catalog cursor is invalid.");
        }
    }

    public static SessionCatalogPage Page(
        IEnumerable<SessionSnapshot> source,
        string? cursor,
        int limit,
        bool includeArchived)
    {
        if (limit is < 1 or > 100)
        {
            throw AgentCoreErrors.Validation("Catalog limit must be between 1 and 100.");
        }

        long? afterMs = null;
        Guid afterId = default;
        if (!string.IsNullOrEmpty(cursor))
        {
            (afterMs, afterId) = Decode(cursor);
        }

        var rows = source
            .Where(item => item.DurablyDeletedAt is null)
            .Where(item => includeArchived || item.ArchivedAt is null)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.SessionId)
            .Where(item =>
                afterMs is null
                || item.UpdatedAt.ToUnixTimeMilliseconds() < afterMs
                || (item.UpdatedAt.ToUnixTimeMilliseconds() == afterMs && item.SessionId.CompareTo(afterId) < 0))
            .Take(limit + 1)
            .ToArray();
        var hasMore = rows.Length > limit;
        var page = hasMore ? rows.Take(limit).ToArray() : rows;
        var next = hasMore ? Encode(page[^1].UpdatedAt, page[^1].SessionId) : null;
        return new SessionCatalogPage(page, next, hasMore);
    }
}
