using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

/// <summary>Completion intent uses the existing durable tool transcript, bound to its Run.</summary>
public static class SessionCompletionIntent
{
    public static void ValidateOutcome(AgentRun run, SessionSnapshot before, SessionSnapshot next, DateTimeOffset now)
    {
        if (Reason(run) is not { } reason)
        {
            if (next.LifecycleStatus == SessionLifecycleStatus.Completed && next.LifecycleSource == LifecycleTransitionSource.Agent
                && before.LifecycleStatus != SessionLifecycleStatus.Completed)
                throw new AgentCore.Application.Security.AccessDeniedException("session_completion_intent_required");
            return;
        }
        if (!Eligible(run, before, now) || next.LifecycleStatus != SessionLifecycleStatus.Completed
            || next.LifecycleSource != LifecycleTransitionSource.Agent || next.LifecycleReason != reason
            || next.PendingAgentInputIds.Count > 0
            || !next.Entries.Any(e => e.ResponseId == run.ResponseId && e.Role == ConversationRole.Assistant && e.Status == EntryStatus.Completed))
            throw new AgentCore.Application.Security.AccessDeniedException("session_completion_unavailable");
    }
    public static bool Eligible(AgentRun run, SessionSnapshot session, DateTimeOffset now) =>
        run.SessionId == session.SessionId && run.AgentInstanceId == session.AgentInstanceId
        && run.Owner.TenantId is not null && run.Owner.ExecutorPrincipalId is not null
        && run.Status == AgentRunStatus.Running && !run.CancellationRequested
        && run.Claim is { } claim && claim.LeaseExpiresAtUtc > now
        && run.Admission.Activation.Kind == ActivationKind.UserTurn
        && run.Admission.OutputContract == AgentRunOutputContract.ConversationResponse
        && session.Origin.Kind == SessionOriginKind.UserChat && session.Surfaces.HasFlag(SessionSurface.ChatList)
        && session.LifecycleStatus == SessionLifecycleStatus.Active && session.ArchivedAt is null
        && session.DurablyDeletedAt is null && !SessionRetention.IsDue(session, now)
        && session.CompletionPolicy?.AgentCompletion == AgentCompletionAuthority.Allowed;

    public static string Accept(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Count() != 1
            || !arguments.TryGetProperty("reason", out var value) || value.ValueKind != JsonValueKind.String
            || value.GetString() is not { Length: >= 1 and <= 500 } reason || string.IsNullOrWhiteSpace(reason))
            return """{"error":"invalid","message":"Use only a reason of 1–500 characters."}""";
        return JsonSerializer.Serialize(new { sessionCompletionIntent = "accepted", reason });
    }

    public static string? Reason(AgentRun run) => AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)
        ? Reason(messages!) : null;

    public static string? Reason(IReadOnlyList<ModelMessage> messages)
    {
        foreach (var message in messages.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.SessionComplete))
        {
            try
            {
                using var json = JsonDocument.Parse(message.Text);
                if (json.RootElement.TryGetProperty("sessionCompletionIntent", out var accepted) && accepted.GetString() == "accepted"
                    && json.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() is { Length: >= 1 and <= 500 } text)
                    return text;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
