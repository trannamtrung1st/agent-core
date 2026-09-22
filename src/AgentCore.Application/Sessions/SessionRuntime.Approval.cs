using System.Text.Json;
using AgentCore.Application.Events;
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

    private Task HandleApprovalResponseAsync(
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

            if (pending is null)
            {
                input.Completed?.TrySetResult(ResponseApprovalResult.Unknown);
                return Task.CompletedTask;
            }

            if (pending.Decided
                && pending.ApprovalId == input.ApprovalId
                && pending.ResponseId == input.ResponseId)
            {
                input.Completed?.TrySetResult(ResponseApprovalResult.Idempotent);
                return Task.CompletedTask;
            }

            if (pending.ApprovalId != input.ApprovalId
                || pending.ResponseId != input.ResponseId
                || _activeResponseId != input.ResponseId
                || pending.Epoch != _epoch)
            {
                input.Completed?.TrySetResult(ResponseApprovalResult.Stale);
                return Task.CompletedTask;
            }

            pending.Decided = true;
            pending.Decision = input.Decision;
            pending.Completion.TrySetResult(
                input.Decision == ToolApprovalDecision.Approve
                    ? ApprovalWaitResult.Approved
                    : ApprovalWaitResult.Rejected);
            input.Completed?.TrySetResult(ResponseApprovalResult.Accepted);
            return Task.CompletedTask;
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
        using var expiryTimer = _time.CreateTimer(
            _ => completion.TrySetResult(ApprovalWaitResult.Expired),
            null,
            ToolApprovalLimits.Lifetime,
            Timeout.InfiniteTimeSpan);

        ApprovalWaitResult waitResult;
        try
        {
            waitResult = await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
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

        await CompleteWaitingExternalProgressAsync(cause, responseId, operationId, CancellationToken.None)
            .ConfigureAwait(false);

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
