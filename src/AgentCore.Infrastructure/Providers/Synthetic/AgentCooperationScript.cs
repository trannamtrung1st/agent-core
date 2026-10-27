using System.Text.Json;
using System.Text.RegularExpressions;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Offline cooperative fixtures. Every call uses the production claimed Agent tool boundary.</summary>
internal static class AgentCooperationScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var background = request.Context?.Sections.Any(s => s.Id == ContextSectionId.CoreBackground) == true;
        var text = background ? request.Context!.Text(ContextSectionId.RunTask) : request.Messages.LastOrDefault(m => m.Role == ModelRole.User)?.Text;
        if (text is null) return null;
        var match = Regex.Match(text, @"synthetic-(task-delegate|task-fanout|direct-message):([0-9a-fA-F-]{36})");
        if (!match.Success || !Guid.TryParseExact(match.Groups[2].Value, "D", out var target)) return null;
        var name = match.Groups[1].Value.StartsWith("task-", StringComparison.Ordinal) ? "tasks.delegate" : "agent-messages.send";
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        if (match.Groups[1].Value == "task-fanout")
        {
            var assignments = results.Where(m => m.Name == "tasks.delegate").ToArray();
            if (assignments.Length < 3) return Call($"synthetic-fanout-{assignments.Length + 1}", "tasks.delegate", new {
                objective = "synthetic-task-worker: Compare the approved migration source.", title = $"Parallel review {assignments.Length + 1}",
                target = new { instanceId = target }, requestKey = $"synthetic-fanout-{assignments.Length + 1}" });
            if (results.All(m => m.Name != ToolCatalog.ExecutionWait))
            {
                var ids = assignments.Select(m => JsonDocument.Parse(m.Text)).ToArray();
                try
                {
                    var tasks = ids.Where(d => d.RootElement.TryGetProperty("task", out _)).Select(d => d.RootElement.GetProperty("task").GetProperty("taskId").GetGuid()).ToArray();
                    return Call(ToolCatalog.ExecutionWait, new { mode = "tasks", taskIds = tasks, until = "all", timeoutSeconds = 120 });
                }
                finally { foreach (var item in ids) item.Dispose(); }
            }
            return [new ModelTextDelta("Synthetic parallel task outcomes observed. The refused third request was not admitted."), new ModelCompleted(ModelStopReason.Completed)];
        }
        if (name == "tasks.delegate" && text.Contains("review-task", StringComparison.Ordinal))
        {
            if (results.All(m => m.Name != ToolCatalog.RequestAuthorization) && request.Tools?.Any(t => t.Name == ToolCatalog.RequestAuthorization) == true)
                return Call(ToolCatalog.RequestAuthorization, new { operation = "delegate_task", task = new {
                    objective = "synthetic-task-worker: Complete the exact reviewed assignment.", title = "Reviewed delegated task",
                    target = new { instanceId = target }, requestKey = "synthetic-reviewed-delegation" } });
            return [new ModelTextDelta("The exact Task admission outcome was recorded; worker completion is a separate fact."), new ModelCompleted(ModelStopReason.Completed)];
        }
        if (request.Tools?.Any(t => t.Name == name) == true && results.All(m => m.Name != name))
        {
            object arguments = name == "tasks.delegate"
                ? new { objective = text.Contains("chain-task", StringComparison.Ordinal) ? $"synthetic-task-delegate:{target:D} chain-task" : "synthetic-task-worker: Compare the approved migration source.", title = "Synthetic delegated review", target = new { instanceId = target },
                    reportOnCompletion = text.Contains("report-task-result", StringComparison.Ordinal), requestKey = "synthetic-delegation-once" }
                : new { recipientInstanceId = target, text = "synthetic-direct-received: Inspect the authorized inbox and save the result. "
                    + Regex.Match(text, @"bounce:[0-9a-fA-F-]{36}:[0-9a-fA-F-]{36}").Value, requestKey = "synthetic-direct-once" };
            return Call(name, arguments);
        }
        if (background) return Call(ToolCatalog.WorkComplete, new { outcome = "Response", attentionRequired = false, summary = "Synthetic cooperation request recorded through current authority." });
        return [new ModelTextDelta("Synthetic cooperation request recorded."), new ModelCompleted(ModelStopReason.Completed)];
    }
    internal static IReadOnlyList<ModelGenerationEvent>? Receive(ModelRequest request)
    {
        if (request.Context?.Text(ContextSectionId.RunTask)?.StartsWith("synthetic-direct-received:", StringComparison.Ordinal) != true) return null;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        if (request.Tools?.Any(t => t.Name == "agent-messages.list") == true && results.All(m => m.Name != "agent-messages.list")) return Call("agent-messages.list", new { limit = 10 });
        var bounce = Regex.Match(request.Context!.Text(ContextSectionId.RunTask)!, @"bounce:([0-9a-fA-F-]{36}):([0-9a-fA-F-]{36})");
        if (bounce.Success && results.All(m => m.Name != "agent-messages.send"))
            return Call("agent-messages.send", new { recipientInstanceId = Guid.Parse(bounce.Groups[1].Value),
                text = $"synthetic-direct-received: Inspect the authorized inbox. bounce:{bounce.Groups[2].Value}:{bounce.Groups[1].Value}", requestKey = "synthetic-bounce-once" });
        return Call(ToolCatalog.WorkComplete, new { outcome = "Response", attentionRequired = false, summary = "Synthetic direct recipient inspected its authorized inbox. No external effects were performed." });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) =>
        Call("synthetic-cooperation-" + name, name, arguments);
    private static IReadOnlyList<ModelGenerationEvent> Call(string id, string name, object arguments) =>
        [new ModelToolCallEvent(new(id, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
