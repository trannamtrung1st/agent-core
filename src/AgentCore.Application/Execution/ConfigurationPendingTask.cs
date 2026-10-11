using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

/// <summary>A diagnosed task receipt, retained inside the existing durable tool checkpoint.</summary>
public sealed record ConfigurationPendingTask(Guid SourceRunId, string Target, string Goal, long InstanceRevision)
{
    public static string? ReadTarget(string? evidence)
    {
        if (evidence is null) return null;
        using var json = JsonDocument.Parse(evidence);
        return json.RootElement.GetProperty("ContinuationTarget").GetString();
    }

    public static ConfigurationPendingTask? ReadSafe(AgentRun source)
    {
        if (source.Status != AgentRunStatus.Completed || source.Admission.Activation.Kind != ActivationKind.UserTurn
            || source.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || !AgentRunToolCallCheckpoint.TryReadState(source.Checkpoint, out var messages, out var uncertain, out var blocked)
            || uncertain || blocked is not null || messages is null || AgentRunToolCallCheckpoint.PendingCalls(messages).Count != 0) return null;
        // Even a confirmed ordinary write stops automatic continuation. Never infer that a
        // send/create/upload may be repeated from prose or a later cleared effect fence.
        if (messages.Where(m => m.Role == ModelRole.Tool).Any(m => m.Name != "configuration.propose"
            && (!ToolRegistry.TryGet(m.Name ?? "", out var tool) || tool.Effect != ToolEffect.ReadOnly))) return null;
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == "configuration.propose").Reverse())
        {
            try
            {
                using var json = JsonDocument.Parse(message.Text);
                if (!json.RootElement.TryGetProperty("outcome", out var outcome) || outcome.GetString() is not ("applied" or "unchanged")
                    || !json.RootElement.TryGetProperty("pendingTask", out var pending) || pending.ValueKind != JsonValueKind.Object) continue;
                var task = pending.Deserialize<ConfigurationPendingTask>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
                if (task is not null && task.SourceRunId == source.AgentRunId && task.Goal is { Length: > 0 and <= 200 }
                    && source.Admission.Configuration is { } configuration && task.InstanceRevision > configuration.InstanceRevision
                    && ToolRegistry.TryGet(task.Target ?? "", out var target) && target.DefinitionAuthorizable
                    && !ManagementTools.IsManagement(task.Target ?? "")) return task;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
