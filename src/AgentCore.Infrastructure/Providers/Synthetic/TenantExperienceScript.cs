using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Context;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Key-free native fixture. Source inspection, consent, recording and fact reads
// still execute through the ordinary tenant tool and transaction owners.
internal static class TenantExperienceScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        IReadOnlyList<ModelGenerationEvent> Finish(string text, bool changed = false) => request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true
            ? Call(ToolCatalog.WorkComplete, new { outcome = changed ? "Response" : "NoAction", summary = text, attentionRequired = false })
            : [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
        const string prefix = "synthetic-tenant-experience: ";
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User && m.Text.StartsWith(prefix, StringComparison.Ordinal))?.Text
            ?? request.Context?.Sections.FirstOrDefault(s => s.Id == ContextSectionId.RunTask)?.Message.Text ?? SyntheticTaskContext.Text(request);
        var sourceKind = "Session";
        string[] parts;
        if (prompt.StartsWith(prefix, StringComparison.Ordinal)) parts = prompt[prefix.Length..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        else
        {
            var taskText = request.Context?.Sections.FirstOrDefault(s => s.Id == ContextSectionId.RunTask)?.Message.Text ?? prompt;
            JsonDocument task; try { task = JsonDocument.Parse(taskText); } catch (JsonException) { return null; }
            using (task)
            {
                var value = task.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("experienceReview", out var reviewMarker) || reviewMarker.ValueKind != JsonValueKind.True
                    || !value.TryGetProperty("sourceKind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("Session" or "AgentRun")
                    || !value.TryGetProperty("sourceId", out var selected) || selected.ValueKind != JsonValueKind.String) return null;
                sourceKind = kind.GetString()!; parts = ["record", selected.GetString()!];
            }
        }
        if (parts is ["consolidate"]) return Consolidate(request);
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty) return Finish("Select one exact source or Experience observation.");
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        JsonElement? Payload(ModelMessage? message) { if (message is null) return null; using var json = JsonDocument.Parse(message.Text); return json.RootElement.Clone(); }
        var authorization = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization));
        if (authorization is { } denied && denied.TryGetProperty("outcome", out var outcome) && outcome.GetString() != "completed") return Finish("The saved Experience operation was not authorized. It will not be repeated.");
        if (parts[0] == "get")
        {
            if (request.Tools?.Any(t => t.Name == ToolCatalog.ContinuityGet && t.ParametersJson.Contains("Experience", StringComparison.Ordinal)) != true) return Finish("Experience inspection is unavailable for this task.");
            var args = new { kind = "Experience", id }; var observation = Payload(results.LastOrDefault(m => m.Name == ToolCatalog.ContinuityGet));
            if (authorization is { } read && read.TryGetProperty("continuityObservation", out var saved) && saved.ValueKind == JsonValueKind.Object) observation = saved;
            if (observation is null) return Call(ToolCatalog.ContinuityGet, args);
            if (RequiresApproval(observation.Value)) return Call(ToolCatalog.RequestAuthorization, new { operation = "inspect_continuity", continuity = new { tool = ToolCatalog.ContinuityGet, arguments = args } });
            return observation.Value.TryGetProperty("items", out var items) && items.GetArrayLength() == 1 ? Finish("The exact owned Experience was inspected under current access.") : Finish("Experience inspection was unavailable. No change was made.");
        }
        if (parts[0] != "record" || request.Tools?.Any(t => t.Name == TenantExperienceToolContract.Record) != true || request.Tools.Any(t => t.Name == TenantExperienceToolContract.Source) != true)
            return Finish("Experience recording is unavailable for this task.");
        if (authorization is { } applied && applied.TryGetProperty("applied", out var done) && done.GetBoolean()) return Finish("The exact Experience observation was recorded with source provenance.", true);
        var source = Payload(results.LastOrDefault(m => m.Name == TenantExperienceToolContract.Source));
        if (authorization is { } reviewed && reviewed.TryGetProperty("experienceSourceObservation", out var authorized) && authorized.ValueKind == JsonValueKind.Object) source = authorized;
        var sourceArgs = new { sourceKind, sourceId = id };
        if (source is null) return Call(TenantExperienceToolContract.Source, sourceArgs);
        if (RequiresApproval(source.Value)) return Consent(TenantExperienceToolContract.Source, sourceArgs);
        if (!source.Value.TryGetProperty("tenantExperienceSourceRead", out var marker) || marker.ValueKind != JsonValueKind.True) return Finish("The source was unavailable. No Experience was recorded.");
        var recordArgs = new { sourceKind, sourceId = id, throughCursor = source.Value.GetProperty("throughCursor").GetInt64(), goal = "Frontend evidence grouping",
            attempts = new[] { "Inspected the bounded retained conversation" }, decisions = new[] { "Keep the observed grouping explicit" }, outcomes = new[] { "Retained a derived observation" },
            corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(), lessons = new[] { "Inspect actual source provenance before recording Experience." } };
        var recorded = Payload(results.LastOrDefault(m => m.Name == TenantExperienceToolContract.Record));
        if (recorded is null) return Call(TenantExperienceToolContract.Record, recordArgs);
        if (RequiresApproval(recorded.Value)) return Consent(TenantExperienceToolContract.Record, recordArgs);
        return recorded.Value.TryGetProperty("alreadyCompleted", out _) ? Finish("The exact Experience observation is retained with source provenance.", true) : Finish("Experience recording was unavailable. No change was repeated.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Consolidate(ModelRequest request)
    {
        IReadOnlyList<ModelGenerationEvent> Finish(string text, bool changed = false) => request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) == true
            ? Call(ToolCatalog.WorkComplete, new { outcome = changed ? "Response" : "NoAction", summary = text, attentionRequired = false })
            : [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
        if (request.Tools?.Any(t => t.Name == ToolCatalog.WorkComplete) != true || request.Tools.Any(t => t.Name == TenantExperienceToolContract.Consolidate) != true)
            return Finish("Experience maintenance is unavailable for this task. No change was made.");
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        var authorization = results.LastOrDefault(m => m.Name == ToolCatalog.RequestAuthorization);
        if (authorization is not null)
        {
            using var saved = JsonDocument.Parse(authorization.Text); var value = saved.RootElement;
            if (value.TryGetProperty("applied", out var applied) && applied.ValueKind == JsonValueKind.True)
                return Finish("The exact Experience consolidation completed with retained source lineage.", true);
            if (value.TryGetProperty("outcome", out _)) return Finish("The saved Experience consolidation was not applied. It will not be repeated.");
        }
        var previous = request.Messages.SelectMany(m => m.ToolCalls ?? []).LastOrDefault(c => c.Name == TenantExperienceToolContract.Consolidate);
        if (previous is not null)
        {
            var result = results.LastOrDefault(m => m.Name == TenantExperienceToolContract.Consolidate);
            if (result is null) return Finish("The Experience maintenance result is unavailable. It will not be repeated.");
            using var json = JsonDocument.Parse(result.Text);
            if (RequiresApproval(json.RootElement)) return Consent(TenantExperienceToolContract.Consolidate, JsonSerializer.Deserialize<JsonElement>(previous.ArgumentsJson));
            return Finish("Experience maintenance was refused. No change was made.");
        }
        var evidence = request.Context?.Sections.FirstOrDefault(s => s.Id == ContextSectionId.Experience)?.Message.Text;
        const string begin = "BEGIN_TENANT_EXPERIENCE_JSON\n", end = "\nEND_TENANT_EXPERIENCE_JSON";
        var start = evidence?.IndexOf(begin, StringComparison.Ordinal) ?? -1; var finish = evidence?.IndexOf(end, StringComparison.Ordinal) ?? -1;
        if (start < 0 || finish < start + begin.Length) return Finish("No readable Experience candidates were observed. No change was made.");
        using var items = JsonDocument.Parse(evidence![(start + begin.Length)..finish]);
        var candidates = items.RootElement.EnumerateArray().Where(i => i.GetProperty("provenance").GetProperty("scope").GetString() == "Instance")
            .Select(i => i.GetProperty("id").GetGuid()).Distinct().Take(2).ToArray();
        if (candidates.Length != 2) return Finish("There are fewer than two owned Instance observations. No change was made.");
        return Call(TenantExperienceToolContract.Consolidate, new { sourceExperienceIds = candidates, goal = "Consolidated evidence grouping",
            attempts = new[] { "Compared the exact owned Instance observations" }, decisions = new[] { "Retain one bounded observation with immediate source lineage" },
            outcomes = new[] { "One exact consolidation proposed for human review" }, corrections = Array.Empty<string>(), unresolved = Array.Empty<string>(), difficulties = Array.Empty<string>(),
            lessons = new[] { "Keep source qualifications and provenance explicit." } });
    }
    private static bool RequiresApproval(JsonElement value) => value.TryGetProperty("decision", out var decision) && decision.GetString() == "requiresApproval";
    private static IReadOnlyList<ModelGenerationEvent> Consent(string tool, object arguments) => Call(ToolCatalog.RequestAuthorization, new { operation = TenantExperienceToolContract.OperationName(tool), experience = new { tool, arguments } });
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) => [new ModelToolCallEvent(new("tenant-experience-" + name + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(arguments))))[..8], name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
