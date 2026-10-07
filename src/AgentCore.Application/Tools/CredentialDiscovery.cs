using System.Text;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;

namespace AgentCore.Application.Tools;

public sealed record CredentialDiscoveryRequest(string? Cursor, int Limit);

public static class CredentialDiscovery
{
    public static CredentialDiscoveryRequest Parse(JsonElement args)
    {
        if (args.EnumerateObject().Any(p => p.Name is not "cursor" and not "limit"))
            throw AgentCoreErrors.Validation("Credential listing accepts only cursor and limit.");
        var limit = 20;
        if (args.TryGetProperty("limit", out var count)
            && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out limit) || limit is < 1 or > 100))
            throw AgentCoreErrors.Validation("Credential listing limit must be between 1 and 100.");
        string? cursor = null;
        if (args.TryGetProperty("cursor", out var after))
        {
            if (after.ValueKind != JsonValueKind.String) throw AgentCoreErrors.Validation("Credential cursor must be an alias.");
            try { cursor = CredentialRules.Reference(after.GetString()!); }
            catch (ArgumentException) { throw AgentCoreErrors.Validation("Credential cursor must be an alias."); }
        }
        return new(cursor, limit);
    }

    public static string Serialize(IReadOnlyList<BoundCredentialMetadata> metadata, CredentialDiscoveryRequest request, int budget)
    {
        var limit = request.Limit;
        var candidates = metadata.Where(m => request.Cursor is null || string.CompareOrdinal(m.Reference, request.Cursor) > 0)
            .OrderBy(m => m.Reference, StringComparer.Ordinal).Take(limit + 1).ToArray();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string Page(int take) => JsonSerializer.Serialize(new {
            items = candidates.Take(take), hasMore = candidates.Length > take,
            nextCursor = candidates.Length > take && take > 0 ? candidates[take - 1].Reference : null
        }, options);
        for (var take = Math.Min(limit, candidates.Length); take > 0; take--)
        {
            var json = Page(take);
            if (Encoding.UTF8.GetByteCount(json) <= Math.Max(0, budget)) return json;
        }
        // Never emit a non-progressing cursor or truncate a metadata record.
        return candidates.Length == 0 ? Page(0) : """{"error":"output_limit"}""";
    }
}
