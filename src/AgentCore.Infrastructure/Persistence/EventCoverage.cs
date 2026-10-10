using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
namespace AgentCore.Infrastructure.Persistence;
internal static class EventCoverage
{
    internal static Guid? SourceRunId(EventBucketSource source)
    {
        using var data = JsonDocument.Parse(source.DataJson);
        return data.RootElement.ValueKind == JsonValueKind.Object
            && data.RootElement.TryGetProperty("agentRunId", out var id)
            && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var runId) ? runId : null;
    }

    internal static (string Bucket, string Source) Cursor(string? cursor)
    {
        if (cursor is null) return ("", "");
        var parts = cursor.Split(':');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || !Guid.TryParse(parts[1], out _)) throw new ArgumentException("Coverage cursor is invalid.");
        return (parts[0], parts[1]);
    }
    internal static EventCoveragePage Page(IEnumerable<(CoreEventBucket Bucket, IReadOnlyCollection<Guid> Covered)> rows,
        TriggerOwner owner, string? cursor, int limit, bool bounded)
    {
        var after = Cursor(cursor); var items = new List<EventSourceCoverage>(); string? next = null;
        foreach (var (b, covered) in rows)
        {
            var id = b.BucketId.ToString("D");
            if (b.Subscription.Owner != owner || string.CompareOrdinal(id, after.Bucket) < 0) continue;
            foreach (var e in b.Sources.OrderBy(e => e.EventId.ToString("D"), StringComparer.Ordinal))
            {
                if (covered.Contains(e.EventId) || id == after.Bucket && string.CompareOrdinal(e.EventId.ToString("D"), after.Source) <= 0) continue;
                if (items.Count == Math.Clamp(limit, 1, 24)) return new(items, next);
                items.Add(new(b.BucketId, e, b.CompletionCode, b.Subscription.TriggerId)); next = id + ":" + e.EventId.ToString("D");
            }
            next = id + ":ffffffff-ffff-ffff-ffff-ffffffffffff";
        }
        return new(items, bounded ? next : null);
    }
}
