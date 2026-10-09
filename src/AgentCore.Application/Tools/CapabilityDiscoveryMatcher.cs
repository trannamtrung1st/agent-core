using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed record CapabilityLoadResult(IReadOnlyList<string> Loaded, IReadOnlyList<string> AlreadyProjected, string Outcome)
{
    public IReadOnlyList<string> Unavailable { get; init; } = [];
    public string NextStep => Outcome switch
    {
        "load_matched" => "Loaded tools are offered on the next model request. Call the appropriate tool directly; existing policy and approvals still apply.",
        "load_already_projected" => "Call the already offered tool directly; do not load it again.",
        "load_unavailable" => "The requested authorized tool cannot be offered in this execution. Report this restriction; loading cannot change it.",
        "load_over_budget" => "Discovery budget exhausted. Use offered tools or report the missing capability.",
        _ => "No relevant eligible match. Use offered tools, try a different concrete goal or exact name, or report the limitation. Do not repeat this query."
    };
    public string ToJson() => JsonSerializer.Serialize(new {
        outcome = Outcome,
        loaded = Loaded.Select(Describe),
        alreadyProjected = AlreadyProjected.Select(Describe),
        unavailable = Unavailable.Select(n => new { name = n, reason = "Not eligible in this execution." }),
        nextStep = NextStep });
    private static object Describe(string name) => new { name, summary = ToolRegistry.Get(name).Summary };
    // Keep readiness and next-step evidence when the cumulative Run output budget is nearly full.
    internal static string? FitReceipt(string result, int budget)
    {
        using var json = JsonDocument.Parse(result);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outcome", out var outcome)
            || outcome.ValueKind != JsonValueKind.String || !outcome.GetString()!.StartsWith("load_", StringComparison.Ordinal)) return null;
        var loaded = Names("loaded"); var already = Names("alreadyProjected"); var unavailable = Names("unavailable");
        while (true)
        {
            var compact = JsonSerializer.Serialize(new {
                outcome = outcome.GetString(), loaded, alreadyProjected = already, unavailable, truncated = true,
                nextStep = "Follow the outcome; call next request's offered tools or report the blocker." });
            if (System.Text.Encoding.UTF8.GetByteCount(compact) <= budget) return compact;
            if (unavailable.Count > 0) unavailable.RemoveAt(unavailable.Count - 1);
            else if (already.Count > 0) already.RemoveAt(already.Count - 1);
            else if (loaded.Count > 0) loaded.RemoveAt(loaded.Count - 1);
            else return null;
        }
        List<object> Names(string key) => root.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => (object)new { name = item.GetProperty("name").GetString() }).ToList() : [];
    }
    internal static string Failure(string error, string outcome, string message) => JsonSerializer.Serialize(new {
        error, outcome, loaded = Array.Empty<object>(), alreadyProjected = Array.Empty<object>(), unavailable = Array.Empty<object>(), nextStep = message });

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
        var tokens = Words(query);
        var descriptors = ToolRegistry.All.Where(d => d.Discoverable).ToArray();
        // Explicit registered names narrow the request, including names that are not authorized.
        // This prevents a denied exact request from falling back to unrelated category matches.
        var exact = descriptors.Where(d => tokens.Contains(d.Name)).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var categories = descriptors.Select(d => d.Category).ToHashSet(StringComparer.Ordinal);
        var requestedCategories = tokens.Where(categories.Contains).ToHashSet(StringComparer.Ordinal);
        var categoryOnly = tokens.Length > 0 && tokens.All(categories.Contains);
        var projected = ToolProjectionService.Project(definition, context, gate).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var eligible = ToolCatalog.Eligible(definition, context, gate).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var candidates = descriptors.Where(d => eligible.Contains(d.Name)
                && (exact.Count > 0 || requestedCategories.Count == 0 || requestedCategories.Contains(d.Category)))
            .Select(d => (Descriptor: d, Score: Score(d))).Where(p => p.Score > 0).ToArray();
        // Once a name/feature tag matches, incidental prose overlap adds no unrelated results.
        var strongMatch = candidates.Any(p => p.Score >= 50);
        var matched = candidates.Where(p => !strongMatch || p.Score >= 50)
            .OrderByDescending(p => p.Score).ThenBy(p => p.Descriptor.Name, StringComparer.Ordinal).Take(limit).ToArray();
        var loaded = matched.Where(p => !projected.Contains(p.Descriptor.Name)).Select(p => p.Descriptor.Name).ToArray();
        var already = matched.Where(p => projected.Contains(p.Descriptor.Name)).Select(p => p.Descriptor.Name).ToArray();
        // Only echo explicitly requested, Definition-authorized names. Never enumerate restricted inventories.
        var unavailable = exact.Where(n => !eligible.Contains(n) && AgentCore.Application.Agents.RolePermissions.AllowsTool(definition, n))
            .Order(StringComparer.Ordinal).Take(limit - matched.Length).ToArray();
        return new(loaded, already, loaded.Length > 0 ? "load_matched" : already.Length > 0 ? "load_already_projected"
            : unavailable.Length > 0 ? "load_unavailable" : "load_no_match") { Unavailable = unavailable };
        int Score(ToolDescriptor d)
        {
            if (exact.Count > 0) return exact.Contains(d.Name) ? 1000 : 0;
            var names = d.Name.Split('.', '_');
            var summary = Words(d.Summary.ToLowerInvariant());
            var content = tokens.Where(t => !categories.Contains(t)).Sum(t => names.Contains(t) ? 80 : d.Tags.Contains(t) ? 50 : summary.Contains(t) ? 1 : 0);
            if (content == 0 && !categoryOnly) return 0;
            return content + (tokens.Contains(d.Category) ? 10 : 0);
        }
    }

    private static string[] Words(string text) => Regex.Matches(text, "[a-z0-9._]+")
        .Select(m => m.Value.Trim('.')).Where(t => t.Length > 0 && t is not
            ("a" or "an" or "the" or "to" or "and" or "for" or "with" or "this" or "of" or "in" or "is" or "use" or "please" or "my" or "handle"))
        .Distinct(StringComparer.Ordinal).ToArray();
}
