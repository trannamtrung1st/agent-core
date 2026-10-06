using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Key-free deterministic product journeys; no extra mutation/test endpoint.</summary>
internal static class IdentityMaintenanceScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        if (!prompt.Contains("synthetic-maintain-", StringComparison.Ordinal)) return null;
        var thought = request.Messages.Any(m => m.Role == ModelRole.System && m.Text.StartsWith("Bounded thought activation.", StringComparison.Ordinal));
        bool Offers(string name) => request.Tools?.Any(t => t.Name == name) == true;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        var memory = prompt.Contains("synthetic-maintain-memory", StringComparison.Ordinal);
        var tool = memory ? ToolCatalog.MemoryConsolidate : ToolCatalog.ExperienceConsolidate;
        if (!Offers(tool)) return Complete(thought, false, "Consolidation permission is unavailable; no change was made.");
        var mutation = results.LastOrDefault(m => m.Name == tool);
        if (mutation is not null)
        {
            using var data = JsonDocument.Parse(mutation.Text);
            var changed = data.RootElement.TryGetProperty("status", out var status) && status.GetString() == "consolidated";
            return Complete(thought, changed, changed ? "Consolidated repeated retained state with source lineage." : "The selected state could not be consolidated; no further change was made.");
        }
        var query = memory ? "frontend" : "observable completed work";
        var search = results.LastOrDefault(m => m.Name == ToolCatalog.ContinuitySearch);
        if (search is null) return Call(ToolCatalog.ContinuitySearch, new { query, limit = 10 });
        using var searchData = JsonDocument.Parse(search.Text);
        if (!searchData.RootElement.TryGetProperty("result", out var items) || items.ValueKind != JsonValueKind.Array)
            return Complete(thought, false, "Candidates were unavailable; no change was made.");
        var kind = memory ? "Memory" : "Experience";
        var selected = items.EnumerateArray().Where(i => i.GetProperty("kind").GetString() == kind)
            .Where(i => !memory || i.GetProperty("provenance").GetProperty("scope").GetString() == "IdentityUser")
            .Take(3).Select(i => i.GetProperty("id").GetGuid()).Order().ToArray();
        if (selected.Length < 2) return Complete(thought, false, "No safely redundant candidates require consolidation.");
        var inspected = new Dictionary<Guid, string>();
        foreach (var result in results.Where(m => m.Name == ToolCatalog.ContinuityGet))
        {
            using var detail = JsonDocument.Parse(result.Text);
            if (detail.RootElement.TryGetProperty("result", out var root) && root.TryGetProperty("item", out var item))
                inspected[item.GetProperty("id").GetGuid()] = root.GetProperty("content").GetString() ?? "";
        }
        foreach (var id in selected) if (!inspected.ContainsKey(id)) return Call(ToolCatalog.ContinuityGet, new { kind, id });
        if (memory)
        {
            // Deliberately conservative fixture: even related contradictory/qualified content no-ops.
            var contents = selected.Select(id => { using var d = JsonDocument.Parse(inspected[id]); return d.RootElement.GetProperty("content").GetString(); }).ToArray();
            if (contents.Any(c => c != "Prefer TypeScript for frontend examples."))
                return Complete(thought, false, "Conflicting or differently qualified memories need clarification; no consolidation was made.");
            return Call(tool, new { sourceMemoryIds = selected, kind = "Preference", subject = "Frontend examples", content = contents[0] });
        }
        return Call(tool, new { sourceExperienceIds = selected, goal = "Review repeated observable completed work",
            attempts = new[] { "Worked through recorded requests" }, decisions = Array.Empty<string>(), outcomes = new[] { "Completed observable source checkpoints" },
            corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(),
            lessons = new[] { "Verify observable state before acting and confirm each outcome; these observations do not prove every failure has the same cause." } });
    }
    private static IReadOnlyList<ModelGenerationEvent> Complete(bool thought, bool changed, string summary) => thought
        ? Call(ToolCatalog.WorkComplete, new { outcome = changed ? "ActionCompleted" : "NoAction", summary, attentionRequired = false })
        : [new ModelTextDelta(summary), new ModelCompleted(ModelStopReason.Completed)];
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object args) =>
        [new ModelToolCallEvent(new("maintenance-" + name + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(args))))[..8], name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
