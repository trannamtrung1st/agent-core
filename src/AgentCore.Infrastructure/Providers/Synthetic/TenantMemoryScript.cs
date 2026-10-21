using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Native key-free Memory consent journey. Every operation still crosses the real
// tool boundary, human decision and owning Memory/approval/Run transactions.
internal static class TenantMemoryScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.StartsWith("synthetic-tenant-memory:", StringComparison.Ordinal))?.Text ?? SyntheticTaskContext.Text(request);
        if (request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("manage_memory", StringComparison.Ordinal)) != true || !prompt.StartsWith("synthetic-tenant-memory:", StringComparison.Ordinal)) return null;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        JsonElement? Payload(ModelMessage? message) { if (message is null) return null; using var json = JsonDocument.Parse(message.Text); return json.RootElement.Clone(); }
        var authorization = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization));
        if (authorization is { } saved && saved.TryGetProperty("applied", out var applied) && applied.GetBoolean())
            return Finish(prompt.EndsWith("forget", StringComparison.Ordinal) ? "The exact learned Memory was forgotten. Other retained data remains." : "The exact learned Memories were consolidated with source lineage.");
        if (authorization is { } denied && denied.TryGetProperty("outcome", out var outcome) && outcome.GetString() != "completed")
            return Finish("The saved Memory operation was not applied. It will not be repeated.");
        var search = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.ContinuitySearch));
        if (authorization is { } reviewed && (reviewed.TryGetProperty("continuityObservation", out var observation) || reviewed.TryGetProperty("memoryObservation", out observation)) && observation.ValueKind == JsonValueKind.Object) search = observation;
        var searchArgs = new { query = "Frontend", limit = 10 };
        if (search is null) return Call(ToolCatalog.ContinuitySearch, searchArgs);
        if (!search.Value.TryGetProperty("items", out var items))
            return search.Value.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval"
                ? Consent(true, ToolCatalog.ContinuitySearch, searchArgs) : Finish("Memory inspection was unavailable. No change was made.");
        var selected = items.EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "Memory" && i.GetProperty("provenance").GetProperty("scope").GetString() == "identityUser").Take(2).ToArray();
        if (prompt.EndsWith("forget", StringComparison.Ordinal)) return selected.Length == 0 ? Finish("No matching retained Memory requires forgetting.")
            : Consent(false, ToolCatalog.MemoryForget, new { memoryId = selected[0].GetProperty("id").GetGuid() });
        if (selected.Length != 2) return Finish("No safely redundant owned Memories require consolidation.");
        foreach (var item in selected)
        {
            var id = item.GetProperty("id").GetGuid();
            if (!results.Where(m => m.Name == ToolCatalog.ContinuityGet).Select(Payload).Any(p => p is { } v && v.TryGetProperty("items", out var values)
                && values.EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == id))) return Call(ToolCatalog.ContinuityGet, new { kind = "Memory", id });
        }
        return Consent(false, ToolCatalog.MemoryConsolidate, new { sourceMemoryIds = selected.Select(i => i.GetProperty("id").GetGuid()).Order().ToArray(),
            kind = "Preference", subject = "Frontend examples", content = "Prefer TypeScript for frontend examples." });
    }
    private static IReadOnlyList<ModelGenerationEvent> Consent(bool read, string tool, object arguments) => Call(ToolCatalog.RequestAuthorization,
        read ? new { operation = "inspect_continuity", continuity = new { tool, arguments } } : (object)new { operation = "manage_memory", memory = new { tool, arguments } });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) => [new ModelToolCallEvent(new("tenant-memory-" + name + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments))))[..8], name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
