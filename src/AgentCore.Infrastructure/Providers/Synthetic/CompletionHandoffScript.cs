using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Offline acceptance journeys exercise production tools, inbox transactions and coordinator wakeups.</summary>
internal static class CompletionHandoffScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text ?? "";
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        ModelMessage? Result(string id) => results.LastOrDefault(m => m.ToolCallId == id);
        if (prompt.Contains("[test:completion-child]", StringComparison.Ordinal))
        {
            if (Result("child-duration") is null) return Call("child-duration", ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 5 });
            return Call("child-complete", ToolCatalog.WorkComplete, new { summary = "Verified background evidence for the active parent Run.", outcome = "NeedsAttention", attentionRequired = true });
        }
        if (prompt.Contains("[test:execution-wait]", StringComparison.Ordinal))
            return Result("duration-wait") is null ? Call("duration-wait", ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 12 }) : Answer("The requested duration elapsed on the same Run.");
        var handoff = prompt.Contains("[test:completion-handoff]", StringComparison.Ordinal);
        var unhandled = prompt.Contains("[test:completion-unhandled]", StringComparison.Ordinal);
        var timeout = prompt.Contains("[test:completion-timeout]", StringComparison.Ordinal);
        if (!handoff && !unhandled && !timeout) return null;
        if (Result("handoff-start") is not { } started) return Call("handoff-start", ToolCatalog.BackgroundStart,
            new { objective = "[test:completion-child] Verify the requested evidence and finish explicitly.", title = "Evidence for current Run", reportCompletion = true });
        using var start = JsonDocument.Parse(started.Text);
        if (!start.RootElement.TryGetProperty("backgroundSessionId", out var child)) return Answer("Background admission was denied.");
        var childId = child.GetString();
        if (Result("handoff-before") is null) return Call("handoff-before", ToolCatalog.BackgroundInspect, new { backgroundSessionId = childId });
        if (Result("handoff-wait") is null) return unhandled
            ? Call("handoff-wait", ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 8 })
            : Call("handoff-wait", ToolCatalog.ExecutionWait, new { mode = "background", backgroundSessionIds = new[] { childId }, until = "all", timeoutSeconds = timeout ? 1 : 120 });
        if (unhandled) return Answer("The active parent Run finished. The unhandled result can now be reported.");
        if (timeout) return Answer("The background wait timed out normally. The child remains available in Background work.");
        if (Result("handoff-inspect") is not { } inspected) return Call("handoff-inspect", ToolCatalog.BackgroundInspect, new { backgroundSessionId = childId });
        using var inspection = JsonDocument.Parse(inspected.Text);
        if (!inspection.RootElement.TryGetProperty("inbox", out var inbox) || inbox.ValueKind != JsonValueKind.Object) return Answer("The result is unavailable.");
        if (Result("handoff-take") is not { } taken) return Call("handoff-take", ToolCatalog.BackgroundTake,
            new { backgroundSessionId = childId, revision = inbox.GetProperty("revision").GetInt64() });
        using var take = JsonDocument.Parse(taken.Text);
        if (Result("handoff-ack") is null) return Call("handoff-ack", ToolCatalog.BackgroundAcknowledge,
            new { backgroundSessionId = childId, revision = take.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64(),
                token = take.RootElement.GetProperty("token").GetString(), usage = "Used the verified background evidence in this final answer." });
        return Answer("I used the verified background result in this answer on the original Run.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string id, string name, object args) =>
        [new ModelToolCallEvent(new(id, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Answer(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
