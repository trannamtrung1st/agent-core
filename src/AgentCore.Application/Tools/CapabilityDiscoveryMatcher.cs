using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record CapabilityLoadResult(IReadOnlyList<string> Loaded, IReadOnlyList<string> AlreadyProjected, string Outcome)
{
    public string ToJson() => JsonSerializer.Serialize(new {
        loaded = Loaded.Select(n => new { name = n, summary = ToolRegistry.Get(n).Summary }),
        alreadyProjected = AlreadyProjected, unavailable = Array.Empty<string>() });
}

public static class CapabilityDiscoveryMatcher
{
    public const int MaxCalls = 8;
    public static CapabilityLoadResult Load(AgentDefinition definition, AgentContext context, IToolConfigurationGate gate, JsonElement args, int calls)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Select(p => p.Name).Distinct().Count() != args.EnumerateObject().Count()
            || args.EnumerateObject().Any(p => p.Name is not ("query" or "limit"))
            || !args.TryGetProperty("query", out var q) || q.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(q.GetString()) || q.GetString()!.Length > 200 || q.GetString()!.Contains('*'))
            throw AgentCoreErrors.Validation("Capability loading requires a short concrete query and optional limit from 1 to 8.");
        var limit = 4;
        if (args.TryGetProperty("limit", out var l) && (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out limit) || limit is < 1 or > 8))
            throw AgentCoreErrors.Validation("Capability match limit must be from 1 to 8.");
        if (calls >= MaxCalls) return new([], [], "load_over_budget");
        var query = q.GetString()!.Trim().ToLowerInvariant();
        var tokens = Regex.Matches(query, "[a-z0-9._]+").Select(m => m.Value).Where(t => t is not ("a" or "an" or "the" or "to" or "and" or "for" or "with" or "this" or "of" or "in" or "is" or "use")).Distinct().ToArray();
        var projected = ToolProjectionService.Project(definition, context, gate).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var matched = ToolCatalog.Eligible(definition, context, gate).Select(t => ToolRegistry.Get(t.Name)).Where(d => d.Discoverable)
            .Select(d => (Descriptor: d, Score: Score(d))).Where(p => p.Score > 0)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Descriptor.Name, StringComparer.Ordinal).ToArray();
        var loaded = matched.Where(p => !projected.Contains(p.Descriptor.Name)).Take(limit).Select(p => p.Descriptor.Name).ToArray();
        var already = matched.Where(p => projected.Contains(p.Descriptor.Name)).Take(limit).Select(p => p.Descriptor.Name).ToArray();
        return new(loaded, already, loaded.Length > 0 ? "load_matched" : already.Length > 0 ? "load_already_projected" : "load_no_match");
        int Score(ToolDescriptor d) => query == d.Name ? 1000 : tokens.Sum(t => t == d.Category ? 100 : d.Tags.Contains(t) ? 50
            : d.Name.Split('.','_').Contains(t) ? 20 : Regex.IsMatch(d.Summary.ToLowerInvariant(), @"\b" + Regex.Escape(t) + @"\b") ? 1 : 0);
    }
}
