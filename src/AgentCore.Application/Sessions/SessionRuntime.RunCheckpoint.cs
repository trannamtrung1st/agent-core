using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private EventContext WorkerContext(EventContext cause) => NewContext(cause.EventId) with
    { Epoch = cause.Epoch, AgentRunGeneration = cause.AgentRunGeneration };

    private async Task RequireProviderRunAuthorityAsync(EventContext cause, Guid responseId, CancellationToken ct)
    {
        // Worker-side checks are read-only. Only mailbox handlers update the bound Run.
        var bound = _boundAgentRun;
        if (bound is null || bound.ResponseId != responseId || cause.Epoch != _epoch)
            throw AgentCoreErrors.Conflict("Model request no longer belongs to the active Run.");
        if (_runAuthority is not null && await _runAuthority.CurrentDefinitionAsync(bound, ct).ConfigureAwait(false) is null)
            throw AgentCoreErrors.Validation("Current owner, source policy or model no longer permits this Run.");
        var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct).ConfigureAwait(false);
        if (current is not { Status: AgentRunStatus.Running, Claim: { } claim, CancellationRequested: false }
            || claim.Generation != cause.AgentRunGeneration || claim.LeaseExpiresAtUtc <= _time.GetUtcNow()
            || _boundAgentRun?.AgentRunId != bound.AgentRunId || cause.Epoch != _epoch)
            throw AgentCoreErrors.Conflict("Model request generation is superseded.");
    }

    private async Task<bool> OwnsWorkerAsync(EventContext context, Guid responseId, CancellationToken ct, bool terminalDeadline = false)
    {
        if (_boundAgentRun is not { Status: AgentRunStatus.Running, Claim: { } claim } bound
            || bound.ResponseId != responseId || context.AgentRunGeneration != claim.Generation) return false;
        var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct).ConfigureAwait(false);
        if (current is not { Status: AgentRunStatus.Running, Claim: { } currentClaim, CancellationRequested: false }
            || currentClaim.Generation != context.AgentRunGeneration || !terminalDeadline && currentClaim.LeaseExpiresAtUtc <= _time.GetUtcNow()) return false;
        // Only a terminal deadline failure may settle an expired, still-current generation.
        // No model output or effect is admitted, and a reclaimed/cancelled Run still fails the fences above.
        if (terminalDeadline && currentClaim.LeaseExpiresAtUtc <= _time.GetUtcNow())
        {
            _boundAgentRun = current;
            return true;
        }
        if (currentClaim.LeaseExpiresAtUtc - _time.GetUtcNow() < TimeSpan.FromMinutes(1))
            current = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                new AgentRunCommand.Renew(current.Revision, _time.GetUtcNow(), currentClaim.Generation,
                    _time.GetUtcNow() + AgentRunCoordinator.ClaimDuration), ct).ConfigureAwait(false);
        _boundAgentRun = current;
        return true;
    }

    private async Task SaveRunCheckpointAsync(EventContext cause, Guid responseId, IReadOnlyList<ModelMessage> messages,
        int steps, int outputBytes, DateTimeOffset? deadline, CancellationToken ct, string? protocolRepairReason = null)
    {
        var run = _boundAgentRun ?? throw AgentCoreErrors.Conflict("AgentRun ownership is unavailable.");
        var observationRequired = run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            && run.SideEffect.ActionHash is not null;
        AgentRunToolCallCheckpoint.TryReadState(run.Checkpoint, out _, out _, out var blockedActionHash);
        if (observationRequired && AgentRunActionHash.MatchesBrowserInteraction(run.Checkpoint?.PayloadJson, run.SideEffect.ActionHash))
            blockedActionHash ??= run.SideEffect.ActionHash;
        var reserve = IsInitialBackgroundRun && AgentRunToolCallCheckpoint.PendingCalls(messages).Any(call => call.Name == ToolCatalog.WorkComplete)
            ? 0 : AgentRunToolCallCheckpoint.CompletionReserve(TriggerKind.UserTurn);
        if (!AgentRunToolCallCheckpoint.TryWriteWithReserve(messages, observationRequired,
            blockedActionHash, reserve,
            out var payload, run.LoadedCapabilityIds, run.CapabilityLoadCount, protocolRepairReason: protocolRepairReason))
            throw AgentCoreErrors.Validation("AgentRun checkpoint capacity reached.");
        var remaining = deadline is null ? 0 : Math.Max(0, (int)Math.Min(int.MaxValue, (deadline.Value - _time.GetUtcNow()).TotalMilliseconds));
        await RequestAgentRunCommandAsync(cause, responseId, (current, now) => new AgentRunCommand.Checkpoint(
            current.Revision, now, current.Claim!.Generation, new AgentRunCheckpoint(payload, steps, outputBytes, remaining), null), ct).ConfigureAwait(false);
    }

    private Task<AgentRun?> MarkRunEffectAsync(EventContext cause, Guid responseId, ModelToolCall call,
        string hash, AgentRunSideEffectDisposition disposition, CancellationToken ct) =>
        RequestAgentRunCommandAsync(cause, responseId, (run, now) => new AgentRunCommand.MarkSideEffect(
            run.Revision, now, run.Claim!.Generation, disposition, call.Id, hash), ct);

    private static bool BrowserDialogPending(string text)
    {
        try { using var json = JsonDocument.Parse(text); return json.RootElement.ValueKind == JsonValueKind.Object
            && json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "dialog_pending"; }
        catch (JsonException) { return false; }
    }

    private static bool RejectedEffect(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            return json.RootElement.ValueKind == JsonValueKind.Object && (
                json.RootElement.TryGetProperty("error", out _) ||
                json.RootElement.TryGetProperty("saved", out var saved) && saved.ValueKind == JsonValueKind.False ||
                json.RootElement.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.False);
        }
        catch (JsonException) { return false; }
    }
}
