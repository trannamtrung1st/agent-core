using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

// Offline fixture commands use the same durable tools and HTTP review as ordinary execution.
internal static class CompletionReadScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var prompt = SyntheticTaskContext.Text(request);
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        ModelMessage? Result(string id) => results.LastOrDefault(m => m.ToolCallId == id);
        bool Offered(string name) => request.Tools?.Any(t => t.Name == name) == true;
        if (prompt.Contains("[test:completion-read-child]", StringComparison.Ordinal))
        {
            if (Result("read-child-file") is null && Offered(ToolCatalog.ArtifactsCreate))
                return Call("read-child-file", ToolCatalog.ArtifactsCreate, new { displayName = "Verified evidence thử nghiệm.txt", contentType = "text/plain", content = "Undisclosed Synthetic fixture file body." });
            return Call("read-child-finish", ToolCatalog.WorkComplete, new { summary = "Saved Synthetic background evidence. Literal text: <script>not executable</script>. No file contents were returned.", outcome = "Response", attentionRequired = false });
        }
        if (!prompt.Contains("[test:completion-read-review]", StringComparison.Ordinal) && Result("read-consent") is null) return null;
        if (Result("read-start") is not { } started)
            return Offered(ToolCatalog.BackgroundStart) ? Call("read-start", ToolCatalog.BackgroundStart,
                new { objective = "[test:completion-read-child] Prepare one saved Synthetic result and related file metadata.", title = "Saved evidence for exact read", reportCompletion = true })
                : Answer("Background creation is not offered for this task.");
        using var start = JsonDocument.Parse(started.Text);
        if (!start.RootElement.TryGetProperty("backgroundSessionId", out var child)) return Answer("The background task was not created.");
        var session = child.GetGuid();
        if (Result("read-duration") is null) return Call("read-duration", ToolCatalog.ExecutionWait, new { mode = "duration", seconds = 8 });
        if (Result("read-consent") is not { } authorized)
            return Offered(ToolCatalog.RequestAuthorization) ? Call("read-consent", ToolCatalog.RequestAuthorization,
                new { operation = "inspect_background_completion", read = new { backgroundSessionId = session } })
                : Answer("Exact completion-read authorization is not offered for this task.");
        using var consent = JsonDocument.Parse(authorized.Text);
        if (!consent.RootElement.TryGetProperty("readAuthorized", out var allowed) || allowed.ValueKind != JsonValueKind.True
            || !consent.RootElement.TryGetProperty("completion", out var observed) || observed.ValueKind != JsonValueKind.Object)
            return Answer("The exact background result was not delivered by this authorization request.");
        if (Result("read-inspect") is not { } inspected) return Call("read-inspect", ToolCatalog.BackgroundInspect, new { backgroundSessionId = session });
        using var inspection = JsonDocument.Parse(inspected.Text);
        if (!inspection.RootElement.TryGetProperty("inbox", out var inbox) || inbox.ValueKind != JsonValueKind.Object)
            return Answer("The reviewed result is unavailable for completion accounting.");
        if (Result("read-take") is not { } taken) return Call("read-take", ToolCatalog.BackgroundTake, new { backgroundSessionId = session, revision = inbox.GetProperty("revision").GetInt64() });
        using var take = JsonDocument.Parse(taken.Text);
        if (!take.RootElement.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
            return Answer("The reviewed completion could not be claimed.");
        if (Result("read-ack") is not { } acknowledged) return Call("read-ack", ToolCatalog.BackgroundAcknowledge,
            new { backgroundSessionId = session, revision = take.RootElement.GetProperty("inbox").GetProperty("revision").GetInt64(), token = token.GetGuid(), usage = "Use this exact reviewed Synthetic result in the saved answer." });
        using var ack = JsonDocument.Parse(acknowledged.Text);
        return ack.RootElement.TryGetProperty("acknowledgmentPending", out var staged) && staged.ValueKind == JsonValueKind.True
            ? Answer("The exact reviewed background result was used in this answer. File contents were not read.")
            : Answer("The result was read, but its completion accounting was not acknowledged.");
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string id, string name, object args) =>
        [new ModelToolCallEvent(new(id, name, JsonSerializer.Serialize(args))), new ModelCompleted(ModelStopReason.ToolCalls)];
    private static IReadOnlyList<ModelGenerationEvent> Answer(string text) => [new ModelTextDelta(text), new ModelCompleted(ModelStopReason.Completed)];
}
