using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Native offline scenario; the normal tool, consent, source and Run owners execute it.
internal static class TenantContinuityScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.StartsWith("synthetic-tenant-continuity:", StringComparison.Ordinal))?.Text ?? SyntheticTaskContext.Text(request);
        if (!prompt.StartsWith("synthetic-tenant-continuity:", StringComparison.Ordinal)
            || request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization && t.ParametersJson.Contains("inspect_continuity", StringComparison.Ordinal)) != true) return null;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        JsonElement? Payload(ModelMessage? message) { if (message is null) return null; using var doc = JsonDocument.Parse(message.Text); return doc.RootElement.Clone(); }
        var authorization = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization));
        if (authorization is { } denied && denied.TryGetProperty("outcome", out var outcome) && outcome.GetString() != "completed") return Finish("Historical read was not authorized.");
        var search = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.ContinuitySearch));
        if (authorization is { } approved && approved.TryGetProperty("continuityObservation", out var saved) && saved.ValueKind == JsonValueKind.Object) search = saved;
        var searchArgs = new { query = "Frontend", limit = 10 };
        if (search is null) return Call(ToolCatalog.ContinuitySearch, searchArgs);
        if (!search.Value.TryGetProperty("items", out var items)) return search.Value.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval"
            ? Consent(ToolCatalog.ContinuitySearch, searchArgs) : Finish("Historical read was not authorized.");
        var sessions = items.EnumerateArray().Where(i => i.GetProperty("kind").GetString() == "Session").ToArray();
        if (sessions.Length == 0) return Finish("No readable historical Session matched the query.");
        var source = sessions[0]; var id = source.GetProperty("id").GetGuid();
        if (!results.Where(m => m.Name == ToolCatalog.ContinuityGet).Select(Payload).Any(p => p is { } value && value.TryGetProperty("items", out var entries)
            && entries.EnumerateArray().Any(i => i.GetProperty("id").GetGuid() == id && i.TryGetProperty("entries", out var page) && page.ValueKind == JsonValueKind.Array)))
            return Call(ToolCatalog.ContinuityGet, new { kind = "Session", id, afterEntrySequence = source.GetProperty("provenance").GetProperty("afterEntrySequence").GetInt64(), limit = source.GetProperty("provenance").GetProperty("limit").GetInt32() });
        return Finish("Historical source inspected under current access.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Consent(string tool, object arguments) => Call(ToolCatalog.RequestAuthorization,
        new { operation = "inspect_continuity", continuity = new { tool, arguments } });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) => [new ModelToolCallEvent(new("tenant-continuity-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Finish(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
