using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Credential-free fixtures for independent work completion and existing-conversation reports.</summary>
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
            && task.Contains("synthetic-background-semantic-", StringComparison.Ordinal))
        {
            if (request.Context.Sections.All(section => section.Id != ContextSectionId.CoreBackgroundFinalization)
                || task.Contains("synthetic-background-semantic-exhausted", StringComparison.Ordinal))
                return [new ModelFailed(new(ProviderErrorCode.InvalidResponse, "Synthetic invalid response blocks.",
                    FailureReason: ProviderFailureReason.InvalidBlocks, ResponseChannel: ProviderResponseChannel.ResponseFunction))];
            return Call(ToolCatalog.WorkComplete, new { outcome = "NeedsAttention", attentionRequired = true,
                summary = "Response format recovery completed. Research is incomplete; no new tools or external actions were performed." });
        }
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
        return null;
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) =>
        [new ModelToolCallEvent(new("background-fixture-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
