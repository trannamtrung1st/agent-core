using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Credential-free fixture journeys use the real background.start and report paths.</summary>
internal static class BackgroundSessionScript
{
    private const string ReportPrefix = "Report the following bounded background completion to this Session's user.";
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var report = request.Messages.LastOrDefault(message => message.Role == ModelRole.System && message.Text.StartsWith(ReportPrefix, StringComparison.Ordinal));
        if (report is not null)
        {
            var evidence = SyntheticTaskContext.Text(request);
            var jsonStart = evidence.IndexOf('{');
            try
            {
                using var data = JsonDocument.Parse(evidence[jsonStart..]);
                var summary = data.RootElement.TryGetProperty("completions", out var batch) ? string.Join(" ", batch.EnumerateArray().Select(i => i.GetProperty("summary").GetString()))
                    : data.RootElement.GetProperty("summary").GetString() ?? "Background work ended.";
                return [new ModelTextDelta("Background work: " + summary), new ModelCompleted(ModelStopReason.Completed)];
            }
            catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
            { return [new ModelTextDelta("Background work ended. Open Background work to inspect the result."), new ModelCompleted(ModelStopReason.Completed)]; }
        }
        if (request.Messages.Any(message => message.Text.Contains("synthetic-background-prose", StringComparison.Ordinal)))
            return [new ModelTextDelta("Fixture prose without an explicit completion."), new ModelCompleted(ModelStopReason.Completed)];
        var task = SyntheticTaskContext.Text(request);
        if (request.Context?.Sections.Any(section => section.Id == ContextSectionId.CoreBackground) == true
            && task.Contains("synthetic-background-completion-", StringComparison.Ordinal))
        {
            if (request.Context.Sections.All(section => section.Id != ContextSectionId.CoreBackgroundFinalization))
                return [new ModelTextDelta("Collected fixture findings."), new ModelCompleted(ModelStopReason.Completed)];
            var corrected = request.Messages.Any(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.WorkComplete);
            return [new ModelToolCallEvent(new("completion-" + request.Messages.Count, ToolCatalog.WorkComplete,
                JsonSerializer.Serialize(new { outcome = "Response", attentionRequired = false,
                    summary = corrected && task.Contains("synthetic-background-completion-correct", StringComparison.Ordinal)
                        ? "Collected digest; no further actions were taken." : new string('x', 4442) }))),
                new ModelCompleted(ModelStopReason.ToolCalls)];
        }
        // Background work never fans out, even when the original objective contains this fixture marker.
        if (request.Messages.Any(message => message.Role == ModelRole.System && message.Text.StartsWith("Bounded background Session task.", StringComparison.Ordinal))) return null;
        var prompt = SyntheticTaskContext.Text(request);
        if (prompt.Contains("[test:followup-file]", StringComparison.Ordinal))
        {
            var userIndex = request.Messages.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            var results = request.Messages.Skip(userIndex + 1).Where(m => m.Role == ModelRole.Tool).ToArray();
            var file = results.LastOrDefault(m => m.Name == ToolCatalog.ArtifactsCreateFromWorkspace);
            if (file is not null) return [new ModelTextDelta("Follow-up report created: " + prompt), new ModelCompleted(ModelStopReason.Completed)];
            return results.Any(m => m.Name == ToolCatalog.WorkspaceWrite)
                ? Call(ToolCatalog.ArtifactsCreateFromWorkspace, new { path = "/working/followup.md", displayName = "followup.md" })
                : Call(ToolCatalog.WorkspaceWrite, new { path = "/working/followup.md", content = prompt });
        }
        if (!prompt.Contains("[test:background-start]", StringComparison.Ordinal)) return null;
        var result = request.Messages.LastOrDefault(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.BackgroundStart);
        if (result is not null)
        {
            using var data = JsonDocument.Parse(result.Text);
            var accepted = data.RootElement.TryGetProperty("started", out var started) && started.ValueKind == JsonValueKind.True;
            return [new ModelTextDelta(accepted ? "Started the background check. You can keep chatting while it runs." : "Background work could not be started with the current permissions or capacity."), new ModelCompleted(ModelStopReason.Completed)];
        }
        if (request.Tools?.Any(tool => tool.Name == ToolCatalog.BackgroundStart) != true)
        {
            if (request.Tools?.Any(tool => tool.Name == ToolCatalog.CapabilitiesLoad) == true && !request.Messages.Any(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.CapabilitiesLoad))
                return Call(ToolCatalog.CapabilitiesLoad, new { query = "start an immediate background task", limit = 1 });
            return [new ModelTextDelta("This Agent is not authorized to start background tasks."), new ModelCompleted(ModelStopReason.Completed)];
        }
        var objective = prompt.Contains("[test:background-completion-correct]", StringComparison.Ordinal) ? "synthetic-background-completion-correct: verify bounded summary correction"
            : prompt.Contains("[test:background-completion-invalid]", StringComparison.Ordinal) ? "synthetic-background-completion-invalid: verify truthful validation failure"
            : prompt.Contains("[test:opensandbox-background]", StringComparison.Ordinal) ? "synthetic-sandbox:demo"
            : prompt.Contains("[test:background-prose]", StringComparison.Ordinal) ? "synthetic-background-prose: verify bounded failure reporting"
            : prompt.Contains("[test:background-files]", StringComparison.Ordinal) ? "synthetic-background-files: create the original task report"
            : "synthetic-automation-attention: check the requested progress and report the outcome";
        return Call(ToolCatalog.BackgroundStart, new { objective, title = "Background progress check", reportCompletion = true });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) =>
        [new ModelToolCallEvent(new("background-fixture-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
