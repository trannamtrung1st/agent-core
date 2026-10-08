using System.Text.Json;
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
            var jsonStart = report.Text.IndexOf('{');
            try
            {
                using var data = JsonDocument.Parse(report.Text[jsonStart..]);
                var summary = data.RootElement.GetProperty("summary").GetString() ?? "Background work ended.";
                return [new ModelTextDelta("Background work: " + summary), new ModelCompleted(ModelStopReason.Completed)];
            }
            catch (Exception exception) when (exception is JsonException or ArgumentOutOfRangeException or InvalidOperationException)
            { return [new ModelTextDelta("Background work ended. Open Background work to inspect the result."), new ModelCompleted(ModelStopReason.Completed)]; }
        }
        // Background work never fans out, even when the original objective contains this fixture marker.
        if (request.Messages.Any(message => message.Role == ModelRole.System && message.Text.StartsWith("Bounded background Session task.", StringComparison.Ordinal))) return null;
        var prompt = request.Messages.LastOrDefault(message => message.Role == ModelRole.User)?.Text ?? "";
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
        return Call(ToolCatalog.BackgroundStart, new { objective = "synthetic-automation-attention: check the requested progress and report the outcome", title = "Background progress check", reportCompletion = true });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) =>
        [new ModelToolCallEvent(new("background-fixture-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
