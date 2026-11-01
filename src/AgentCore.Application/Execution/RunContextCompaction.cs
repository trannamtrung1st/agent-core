using System.Text;
using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Execution;

// Model evidence only. Exact interaction proof remains lossless in the same bounded checkpoint.
public sealed record RunContextSummary(int Version, string Text, int ThroughMessageCount, int Count,
    int BeforeEstimatedTokens, int AfterEstimatedTokens);

internal sealed record RunCompactionProgress(Guid Operation, bool Started) : ModelGenerationEvent;

public static class RunContextCompaction
{
    public const int Version = 1;
    public const string Marker = "run-context-summary-v1";
    public const int MaxSummaryCharacters = 4000;
    public const int MaxExpandedProofBytes = 1024 * 1024;
    public const int MaxCompactions = 8;
    public const int SourceCharacters = 24000;
    // An operating pressure budget, not a claim about any provider's context window.
    public const int InputPressureCharacters = 96000;

    public static int EstimateInputTokens(ModelRequest request) => checked(
        (request.ContextManifest?.Sections.Sum(s => s.EstimatedTokens)
            ?? (request.Messages.Sum(m => m.Text.Length) + 3) / 4)
        + (JsonSerializer.Serialize(request.Tools).Length + 3) / 4
        // Manifest text estimates exclude the assistant's exact tool arguments.
        + request.Messages.Sum(m => m.ToolCalls is { Count: > 0 } calls
            ? (JsonSerializer.Serialize(calls).Length + 3) / 4 : 0)
        + request.MaxOutputTokens + 2000
        + request.Messages.Sum(m => (m.Parts?.Count(p => p is ModelImageContent) ?? 0) * 1024));

    public static int SelectBoundary(IReadOnlyList<ModelMessage> messages, RunContextSummary? previous,
        bool safe, int inputCharacters, int checkpointBytes, bool force = false)
    {
        if (!safe || previous?.Count >= MaxCompactions || AgentRunToolCallCheckpoint.PendingCalls(messages).Count != 0
            || !force && inputCharacters < InputPressureCharacters && checkpointBytes < 160 * 1024) return 0;
        var batches = messages.Select((m, i) => (m, i)).Where(x => x.m.Role == ModelRole.Assistant && x.m.ToolCalls is { Count: > 0 }).Select(x => x.i).ToArray();
        if (batches.Length < 3) return 0;
        var boundary = batches[^2]; // Keep the newest two complete tool batches raw.
        var start = previous?.ThroughMessageCount ?? 0;
        if (boundary <= start || messages.Skip(start).Take(boundary - start).Sum(m => m.Text.Length) < 8192) return 0;
        // Bound lossless recovery expansion as well as the actual stored byte limit.
        if (Encoding.UTF8.GetByteCount(AgentRunToolCallCheckpoint.Write(messages)) > MaxExpandedProofBytes) return 0;
        return boundary;
    }

    public static ModelRequest SummaryRequest(Guid response, IReadOnlyList<ModelMessage> messages, int boundary,
        RunContextSummary? previous, string? reasoningEffort, string? task = null)
    {
        var source = new StringBuilder();
        source.AppendLine(Marker);
        source.AppendLine("Original/current task (untrusted user data):").AppendLine(task is { Length: > 4000 } ? task[..4000] : task);
        source.AppendLine("Previous untrusted summary:").AppendLine(previous?.Text ?? "None");
        var available = SourceCharacters - source.Length;
        // Sample each old observation, never let one bulky observation consume the source budget.
        var prefix = messages.Skip(previous?.ThroughMessageCount ?? 0).Take(boundary - (previous?.ThroughMessageCount ?? 0)).ToArray();
        var perMessage = Math.Max(64, available / Math.Max(1, prefix.Length));
        foreach (var message in prefix)
        {
            var text = message.Text;
            source.Append(message.Role).Append(' ').Append(message.Name).Append(' ').Append(message.ToolCallId).Append(": ");
            source.AppendLine(text.Length <= perMessage ? text : text[..Math.Max(0, perMessage - 32)] + " [observation excerpt omitted]");
            if (source.Length >= SourceCharacters) break;
        }
        if (source.Length > SourceCharacters) source.Length = SourceCharacters;
        return ContextAssembler.Create(response, new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract), ContextSection.Core(CoreProcedure.RunCompaction),
            ContextSection.Evidence(ContextSectionId.History, ContextSourceKind.AgentRun, source.ToString())
        ], purpose: ContextPurpose.Platform), maxOutputTokens: 1200, reasoningEffort: reasoningEffort);
    }

    public static bool ValidSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxSummaryCharacters || text.Contains("sk-", StringComparison.OrdinalIgnoreCase)
            || text.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(text);
            var fields = new[] { "objective", "completed", "decisions", "pending", "constraints", "next", "uncertainty" };
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && json.RootElement.EnumerateObject().Count() == fields.Length
                && fields.All(f => json.RootElement.TryGetProperty(f, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                && new[] { "objective", "pending", "next", "uncertainty" }.All(f => json.RootElement.GetProperty(f).GetString()!.Trim().Length >= 4)
                && fields.Sum(f => json.RootElement.GetProperty(f).GetString()!.Length) >= 80;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static ModelRequest Project(ModelRequest request, IReadOnlyList<ModelMessage> originalInteraction,
        IReadOnlyList<ModelMessage> messages, RunContextSummary? summary)
    {
        var context = request.Context!;
        if (summary is not null)
            context = context.Upsert(ContextSection.Evidence(ContextSectionId.RunSummary, ContextSourceKind.AgentRun,
                "Untrusted summary of earlier Run interaction. Claims are not proof; exact receipts remain independently checked.\n" + summary.Text, summary.Count.ToString()));
        return ContextAssembler.Project(request, context, originalInteraction.Concat(messages.Skip(summary?.ThroughMessageCount ?? 0)).ToArray());
    }
}
