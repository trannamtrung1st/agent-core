using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Domain.Conversation;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly object _approvalGate = new();
    private PendingToolApproval? _pendingApproval;

    private sealed class PendingToolApproval
    {
        public required Guid ApprovalId { get; init; }
        public required Guid ResponseId { get; init; }
        public required Guid OperationId { get; init; }
        public required Guid Epoch { get; init; }
        public required string ToolName { get; init; }
        public required string Effect { get; init; }
        public required string Summary { get; init; }
        public required IReadOnlyDictionary<string, string> Details { get; init; }
        public required string ActionHash { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public required TaskCompletionSource<ApprovalWaitResult> Completion { get; init; }
        public bool Decided { get; set; }
        public ToolApprovalDecision? Decision { get; set; }
    }

    private enum ApprovalWaitResult
    {
        Approved,
        Rejected,
        Expired,
        Superseded
    }

    public async Task<ResponseApprovalResult?> RespondApprovalAsync(
        Guid expectedResponseId,
        Guid approvalId,
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource<ResponseApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(
                new ApprovalResponseReceived(context, expectedResponseId, approvalId, decision, completed),
                urgent: true))
        {
            completed.TrySetResult(ResponseApprovalResult.Unknown);
            return null;
        }

        return await WaitOrCancelAsync(completed, ResponseApprovalResult.Unknown, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleApprovalResponseAsync(
        ApprovalResponseReceived input,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            PendingToolApproval? pending;
            lock (_approvalGate)
            {
                pending = _pendingApproval;
            }

            if (pending is null && _agentRuns is not null && _boundAgentRun is { Status: AgentRunStatus.WaitingForApproval, Approval: { } recovered } run
                && recovered.ApprovalId == input.ApprovalId && run.ResponseId == input.ResponseId && recovered.ExpiresAtUtc > _time.GetUtcNow())
            {
                _boundAgentRun = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.DecideApproval(run.Revision,
                    _time.GetUtcNow(), recovered.ApprovalId, recovered.Revision, recovered.ActionHash,
                    input.Decision == ToolApprovalDecision.Approve ? AgentRunApprovalDecision.Approved : AgentRunApprovalDecision.Rejected), CancellationToken.None).ConfigureAwait(false);
                input.Completed?.TrySetResult(ResponseApprovalResult.Accepted);
                return;
            }
            if (pending is null)
            {
                OperationalDiagnostics.RecordApproval("unknown", "unknown", null);
                input.Completed?.TrySetResult(ResponseApprovalResult.Unknown);
                return;
            }

            if (pending.Decided
                && pending.ApprovalId == input.ApprovalId
                && pending.ResponseId == input.ResponseId)
            {
                OperationalDiagnostics.RecordApproval("idempotent", "idempotent", null);
                input.Completed?.TrySetResult(ResponseApprovalResult.Idempotent);
                return;
            }

            if (pending.ApprovalId != input.ApprovalId
                || pending.ResponseId != input.ResponseId
                || _activeResponseId != input.ResponseId
                || pending.Epoch != _epoch)
            {
                OperationalDiagnostics.RecordApproval("stale", "stale", null);
                input.Completed?.TrySetResult(ResponseApprovalResult.Stale);
                return;
            }

            if (_time.GetUtcNow() >= pending.ExpiresAt)
            {
                pending.Completion.TrySetResult(ApprovalWaitResult.Expired);
                input.Completed?.TrySetResult(ResponseApprovalResult.Stale);
                return;
            }

            await ResolveBoundApprovalAsync(input.Decision == ToolApprovalDecision.Approve
                ? AgentRunApprovalDecision.Approved : AgentRunApprovalDecision.Rejected, CancellationToken.None, () =>
                {
                    pending.Decided = true;
                    pending.Decision = input.Decision;
                    input.Completed?.TrySetResult(ResponseApprovalResult.Accepted);
                }).ConfigureAwait(false);
            pending.Decided = true;
            pending.Decision = input.Decision;
            pending.Completion.TrySetResult(
                input.Decision == ToolApprovalDecision.Approve
                    ? ApprovalWaitResult.Approved
                    : ApprovalWaitResult.Rejected);
            // ACK the accepted decision; durable continuation remains owned by this mailbox.
            input.Completed?.TrySetResult(ResponseApprovalResult.Accepted);

            return;
        }
        catch
        {
            input.Completed?.TrySetResult(ResponseApprovalResult.Unknown);
            throw;
        }
    }

    private void ClearPendingApproval(ApprovalWaitResult reason = ApprovalWaitResult.Superseded)
    {
        PendingToolApproval? pending;
        lock (_approvalGate)
        {
            pending = _pendingApproval;
            _pendingApproval = null;
        }

        pending?.Completion.TrySetResult(reason);
    }

    private async Task<ToolApprovalGrant?> WaitForToolApprovalAsync(
        EventContext cause,
        Guid responseId,
        Guid operationId,
        ModelToolCall call,
        JsonElement args,
        string actionHash,
        CancellationToken cancellationToken,
        string? summaryOverride = null,
        IReadOnlyDictionary<string, string>? detailsOverride = null)
    {
        var approvalId = _ids.NewId();
        var expiresAt = _time.GetUtcNow().Add(ToolApprovalLimits.Lifetime);
        var (defaultSummary, defaultDetails) = ToolApprovalPreview.Build(call.Name, args);
        var summary = summaryOverride ?? defaultSummary;
        var details = detailsOverride is null
            ? defaultDetails
            : new Dictionary<string, string>(detailsOverride, StringComparer.Ordinal);
        var wireEffect = ToWireEffect(ToolCatalog.EffectOf(call.Name));
        var completion = new TaskCompletionSource<ApprovalWaitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingToolApproval
        {
            ApprovalId = approvalId,
            ResponseId = responseId,
            OperationId = operationId,
            Epoch = _epoch,
            ToolName = call.Name,
            Effect = wireEffect,
            Summary = summary,
            Details = details,
            ActionHash = actionHash,
            ExpiresAt = expiresAt,
            Completion = completion
        };

        lock (_approvalGate)
        {
            _pendingApproval = pending;
        }

        await RequestAgentRunCommandAsync(cause, responseId, (run, now) =>
            new AgentRunCommand.BeginApproval(run.Revision, now, run.Claim!.Generation, approvalId,
                call.Name, args.GetRawText(), actionHash,
                JsonSerializer.Serialize(new { summary, details }), expiresAt), CancellationToken.None).ConfigureAwait(false);
        var waitStarted = Stopwatch.GetTimestamp();
        OperationalDiagnostics.RecordApproval("waiting", "waiting", null);

        // Arm the deadline before exposing the request. A decision or clock advance can
        // arrive as soon as the output is observed, including while publication awaits.
        using var expiryTimer = _time.CreateTimer(
            _ => completion.TrySetResult(ApprovalWaitResult.Expired),
            null,
            TimeSpan.FromTicks(Math.Max(0, (expiresAt - _time.GetUtcNow()).Ticks)),
            Timeout.InfiniteTimeSpan);

        await PublishAsync(
                new SessionOutput(
                    cause,
                    responseId,
                    new ApprovalRequestedOutput(
                        approvalId,
                        operationId,
                        call.Name,
                        wireEffect,
                        summary,
                        details,
                        expiresAt)),
                CancellationToken.None)
            .ConfigureAwait(false);

        await PublishProgressAsync(
                cause,
                responseId,
                ResponseProgressKind.WaitingExternal,
                ResponseProgressState.Started,
                operationId,
                ResponseProgressMessages.WaitingForApproval,
                CancellationToken.None)
            .ConfigureAwait(false);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ApprovalWaitResult waitResult;
        try
        {
            waitResult = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            OperationalDiagnostics.RecordApproval(
                "superseded",
                "superseded",
                RuntimeTelemetry.ElapsedMs(waitStarted));
            ClearPendingApproval(ApprovalWaitResult.Superseded);
            throw;
        }
        finally
        {
            lock (_approvalGate)
            {
                if (_pendingApproval?.ApprovalId == approvalId)
                {
                    _pendingApproval = null;
                }
            }
        }

        if (waitResult == ApprovalWaitResult.Expired && _agentRuns is not null)
        {
            var expired = await RequestAgentRunCommandAsync(cause, responseId,
                (run, now) => new AgentRunCommand.ExpireApproval(run.Revision, now), CancellationToken.None).ConfigureAwait(false);
            await RequestAgentRunCommandAsync(cause, responseId,
                (run, now) => new AgentRunCommand.Claim(run.Revision, now, _ids.NewId(), now + AgentRunCoordinator.ClaimDuration),
                CancellationToken.None).ConfigureAwait(false);
        }

        await CompleteWaitingExternalProgressAsync(cause, responseId, operationId, CancellationToken.None)
            .ConfigureAwait(false);
        var waitState = waitResult switch
        {
            ApprovalWaitResult.Approved => "approved",
            ApprovalWaitResult.Rejected => "rejected",
            ApprovalWaitResult.Expired => "expired",
            _ => "superseded"
        };
        OperationalDiagnostics.RecordApproval(waitState, waitState, RuntimeTelemetry.ElapsedMs(waitStarted));

        if (waitResult != ApprovalWaitResult.Approved)
        {
            return null;
        }

        return new ToolApprovalGrant(
            approvalId,
            call.Name,
            actionHash,
            responseId,
            operationId,
            _epoch);
    }

    private async Task CompleteWaitingExternalProgressAsync(
        EventContext cause,
        Guid responseId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        await PublishProgressAsync(
                cause,
                responseId,
                ResponseProgressKind.WaitingExternal,
                ResponseProgressState.Completed,
                operationId,
                ResponseProgressMessages.WaitingForApproval,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal PublicPendingApproval? BuildPublicPendingApproval()
    {
        lock (_approvalGate)
        {
            var pending = _pendingApproval;
            if (pending is null && _boundAgentRun is { Status: AgentRunStatus.WaitingForApproval, Approval: { } approval, ResponseId: { } response })
            {
                string summary = "Review the prepared action.";
                IReadOnlyDictionary<string, string> details = new Dictionary<string, string>();
                try
                {
                    using var document = JsonDocument.Parse(approval.Preview);
                    if (document.RootElement.TryGetProperty("summary", out var text)) summary = text.GetString() ?? summary;
                    if (document.RootElement.TryGetProperty("details", out var values))
                        details = JsonSerializer.Deserialize<Dictionary<string, string>>(values.GetRawText()) ?? [];
                }
                catch (JsonException) { }
                return new PublicPendingApproval(approval.ApprovalId, response, approval.ApprovalId, approval.ToolName,
                    ToWireEffect(ToolCatalog.EffectOf(approval.ToolName)), summary, details, approval.ExpiresAtUtc);
            }
            if (pending is null || pending.Decided)
            {
                return null;
            }

            return new PublicPendingApproval(
                pending.ApprovalId,
                pending.ResponseId,
                pending.OperationId,
                pending.ToolName,
                pending.Effect,
                pending.Summary,
                pending.Details,
                pending.ExpiresAt);
        }
    }

    private static string ToWireEffect(ToolEffect effect) => effect switch
    {
        ToolEffect.ReadOnly => "readOnly",
        ToolEffect.Write => "write",
        ToolEffect.SensitiveWrite => "sensitiveWrite",
        ToolEffect.Destructive => "destructive",
        _ => "write"
    };
}
