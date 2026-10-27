using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private bool _tenantWaitSuspended;
    private sealed record CompletionToolResult(string Json, bool Suspended = false);
    private sealed record CompletionToolReceived(EventContext Context, Guid ResponseId, ModelToolCall Call,
        TaskCompletionSource<CompletionToolResult> Completed) : SessionInput(Context);

    private async Task<CompletionToolResult> RequestCompletionToolAsync(EventContext cause, Guid responseId, ModelToolCall call, CancellationToken ct)
    {
        var result = new TaskCompletionSource<CompletionToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new CompletionToolReceived(WorkerContext(cause), responseId, call, result), urgent: true))
            return new(SkillLoadAdmission.Error("stale", "Execution no longer owns this request."));
        return await result.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleCompletionToolAsync(CompletionToolReceived input, CancellationToken ct)
    {
        try
        {
            if (!await OwnsWorkerAsync(input.Context, input.ResponseId, ct).ConfigureAwait(false))
            { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("Conflict", "Execution generation is stale."))); return; }
            if (_runtimeOwner is null) { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("forbidden", "Trusted tenant execution is required."))); return; }
            var parent = _boundAgentRun!;
            var current = _runAuthority is null ? ExecutionDefinition : await _runAuthority.CurrentDefinitionAsync(parent, ct).ConfigureAwait(false);
            if (current is null || !RolePermissions.AllowsTool(current, input.Call.Name) || !RolePermissions.AllowsTool(ExecutionDefinition, input.Call.Name))
            { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("forbidden", "This capability is not authorized."))); return; }
            using var document = JsonDocument.Parse(input.Call.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
            var now = _time.GetUtcNow();
            if (input.Call.Name == ToolCatalog.ExecutionWait)
            {
                var wait = ExecutionWaitContract.Parse(input.Call.Id, args, now);
                _boundAgentRun = await _agentRuns.ApplyAsync(parent.Owner, parent.AgentRunId,
                    new AgentRunCommand.SuspendWait(parent.Revision, now, parent.Claim!.Generation,
                        parent.Checkpoint ?? throw new ArgumentException("Pending wait checkpoint is missing."), wait), ct).ConfigureAwait(false);
                if (_runtimeOwner is not null) _tenantWaitSuspended = true;
                try
                {
                    await PublishProgressAsync(input.Context, input.ResponseId, ResponseProgressKind.WaitingExternal,
                        ResponseProgressState.Started, _ids.NewId(), wait.Mode == AgentRunWaitMode.Duration ? "Waiting for the requested duration…" : "Waiting for the requested task signal…", ct).ConfigureAwait(false);
                }
                finally
                {
                    if (_runtimeOwner is not null) { _activeResponseId = null; _activeEntryId = null; _outputActivity = OutputActivity.Idle; }
                    // Once the wait committed, transport progress cannot turn it back into model work.
                    input.Completed.TrySetResult(new("", true));
                }
                return;
            }
            throw new ArgumentException("Unsupported wait operation.");
        }
        catch (AgentCore.Application.Security.AccessDeniedException e)
        { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("forbidden", e.ReasonCode))); }
        catch (AgentCore.Application.Security.SecurityConflictException e)
        { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("Conflict", e.ReasonCode))); }
        catch (Exception e) when (e is AgentCoreException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { input.Completed.TrySetResult(new(SkillLoadAdmission.Error(e is AgentCoreException core ? core.Code : "ValidationError", e is AgentCoreException ? e.Message : "Provide a supported bounded completion or wait request."))); }
        finally { input.Completed.TrySetResult(new(SkillLoadAdmission.Error("failed", "Completion operation did not finish."))); }
    }

}
