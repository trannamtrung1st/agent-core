using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.Synthetic;

/// <summary>Key-free fixture only. All calls traverse production tool, claim, IAM and persistence boundaries.</summary>
internal static class TaskDelegationScript
{
    internal static IReadOnlyList<ModelGenerationEvent>? Generate(ModelRequest request)
    {
        var task = request.Context?.Sections.FirstOrDefault(s => s.Id == ContextSectionId.RunTask);
        if (task?.Message.Text.Contains("synthetic-task-worker", StringComparison.Ordinal) != true) return null;
        if (request.Context!.Sections.All(s => s.Id != ContextSectionId.CoreBackground)) return null;
        var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
        var question = task.Message.Text.Contains("synthetic-task-question", StringComparison.Ordinal);
        if (Guid.TryParse(task.SourceRevision, out var taskId) && request.Tools?.Any(t => t.Name == "tasks.messages.send") == true
            && results.All(m => m.Name != "tasks.messages.send"))
            return Call("tasks.messages.send", new { taskId, kind = question ? "question" : "progress", text = question ? "Which approved source should I use?" : "Inspected the assigned Synthetic task.", requestKey = "synthetic-progress-once" });
        if (question && Guid.TryParse(task.SourceRevision, out taskId))
        {
            if (results.All(m => m.Name != ToolCatalog.ExecutionWait)) return Call(ToolCatalog.ExecutionWait, new { mode = "tasks", taskIds = new[] { taskId }, until = "any", timeoutSeconds = 120 });
            if (results.All(m => m.Name != "tasks.messages.list")) return Call("tasks.messages.list", new { taskId, limit = 50 });
        }
        var clarified = !question || results.Any(m => m.Name == "tasks.messages.list" && m.Text.Contains("HumanGuidance", StringComparison.Ordinal));
        return Call(ToolCatalog.WorkComplete, new { outcome = clarified ? "Response" : "NeedsAttention", attentionRequired = !clarified,
            summary = clarified ? "Synthetic worker completed the assigned review. No external effects were performed." : "No authorized clarification was observed. The task needs attention." });
    }
    private static IReadOnlyList<ModelGenerationEvent> Call(string name, object arguments) =>
        [new ModelToolCallEvent(new("synthetic-task-" + name, name, JsonSerializer.Serialize(arguments))), new ModelCompleted(ModelStopReason.ToolCalls)];
}
