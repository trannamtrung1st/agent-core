using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record BackgroundStartReceived(EventContext Context, Guid ResponseId, ModelToolCall Call,
        TaskCompletionSource<string> Completed) : SessionInput(Context);

    private async Task<string> RequestBackgroundStartAsync(EventContext context, Guid responseId, ModelToolCall call, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new BackgroundStartReceived(WorkerContext(context), responseId, call, completion), urgent: true))
            return SkillLoadAdmission.Error("stale", "The background request is no longer owned by this turn.");
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleBackgroundStartAsync(BackgroundStartReceived input, CancellationToken ct)
    {
        try
        {
            if (_deactivated || _responseTerminal || _activeResponseId != input.ResponseId
                || input.Context.Epoch != _epoch || _activeResponseTriggerKind != TriggerKind.UserTurn
                || !await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false))
            { input.Completed.TrySetResult(SkillLoadAdmission.Error("stale", "The background request is no longer owned by this turn.")); return; }
            var source = _boundAgentRun!;
            var current = _runAuthority is null ? _snapshot.Definition : await _runAuthority.CurrentDefinitionAsync(source, ct).ConfigureAwait(false);
            if (current is null || !AgentCore.Application.Agents.RolePermissions.AllowsTool(current, ToolCatalog.BackgroundStart)
                || !AgentCore.Application.Agents.RolePermissions.AllowsTool(_snapshot.Definition, ToolCatalog.BackgroundStart))
            { input.Completed.TrySetResult(SkillLoadAdmission.Error("forbidden", "This Agent cannot start background work.")); return; }
            using var document = JsonDocument.Parse(input.Call.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(property => property.Name is not ("objective" or "title" or "reportCompletion"))
                || !args.TryGetProperty("objective", out var objective) || objective.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(objective.GetString()) || objective.GetString()!.Length > AgentRunLimits.MaxBackgroundObjectiveCharacters
                || input.Call.Id.Length is < 1 or > 128
                || args.TryGetProperty("title", out var title) && (title.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(title.GetString()) || title.GetString()!.Length > 80)
                || args.TryGetProperty("reportCompletion", out var report) && report.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { input.Completed.TrySetResult(SkillLoadAdmission.Error("invalid", "Provide a bounded objective, optional title and completion preference.")); return; }
            var proposal = BackgroundSessionAdmissionFactory.ForImmediate(_ids.NewSessionId(), _ids.NewId(), _ids.NewId(),
                _ids.NewId(), _ids.NewId(), _snapshot, source, input.Call.Id, objective.GetString()!.Trim(),
                title.ValueKind == JsonValueKind.String ? title.GetString() : null,
                report.ValueKind != JsonValueKind.False, _time.GetUtcNow());
            var result = await _agentRuns.AdmitImmediateAsync(proposal.Session, proposal.Run, input.Context.AgentRunGeneration!.Value, ct).ConfigureAwait(false);
            input.Completed.TrySetResult(JsonSerializer.Serialize(new { started = true, backgroundSessionId = result.Run.SessionId,
                agentRunId = result.Run.AgentRunId, alreadyStarted = !result.Created }));
        }
        catch (Exception exception) when (exception is AgentCoreException or JsonException or ArgumentException)
        { input.Completed.TrySetResult(SkillLoadAdmission.Error("denied", "Background work could not be admitted with this authority or capacity.")); }
        finally { input.Completed.TrySetResult(SkillLoadAdmission.Error("failed", "Background admission did not complete.")); }
    }
}
