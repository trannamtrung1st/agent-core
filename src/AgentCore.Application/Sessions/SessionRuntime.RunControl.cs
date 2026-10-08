using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record AgentRunControlReceived(EventContext Context, Guid RunId, long ExpectedRevision,
        Guid? ApprovalId, long? ApprovalRevision, string? ActionHash, AgentRunApprovalDecision? Decision,
        TaskCompletionSource<AgentRun> Completed) : SessionInput(Context);

    public async Task<AgentRun> ControlAgentRunAsync(Guid runId, long expectedRevision, Guid? approvalId = null,
        long? approvalRevision = null, string? actionHash = null, AgentRunApprovalDecision? decision = null, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<AgentRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new AgentRunControlReceived(NewContext(), runId, expectedRevision, approvalId, approvalRevision, actionHash, decision, completion), urgent: true))
            throw AgentCoreErrors.Conflict("Session mailbox is unavailable.");
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleAgentRunControlAsync(AgentRunControlReceived input, CancellationToken ct)
    {
        try
        {
            var run = await _agentRuns.GetAsync(RunOwner, input.RunId, ct).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");
            if (run.SessionId != SessionId) throw AgentCoreErrors.NotFound("AgentRun was not found.");
            if (run.Revision != input.ExpectedRevision) throw AgentCoreErrors.Conflict("AgentRun revision is stale.");
            if (input.Decision is { } decision)
            {
                if (decision is not (AgentRunApprovalDecision.Approved or AgentRunApprovalDecision.Rejected)
                    || run.Status != AgentRunStatus.WaitingForApproval || run.Approval is not { } approval
                    || input.ApprovalId != approval.ApprovalId || input.ApprovalRevision != approval.Revision || input.ActionHash != approval.ActionHash)
                    throw AgentCoreErrors.Conflict("Approval identity or revision is stale.");
                if (_boundAgentRun?.AgentRunId == run.AgentRunId && _pendingApproval is not null)
                {
                    _boundAgentRun = run;
                    var accepted = new TaskCompletionSource<ResponseApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    await HandleApprovalResponseAsync(new(input.Context, run.ResponseId!.Value, approval.ApprovalId,
                        decision == AgentRunApprovalDecision.Approved ? ToolApprovalDecision.Approve : ToolApprovalDecision.Reject, accepted), ct).ConfigureAwait(false);
                    if (await accepted.Task.ConfigureAwait(false) != ResponseApprovalResult.Accepted) throw AgentCoreErrors.Conflict("Approval is no longer pending.");
                }
                else
                {
                    run = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.DecideApproval(run.Revision,
                        _time.GetUtcNow(), approval.ApprovalId, approval.Revision, approval.ActionHash, decision), ct).ConfigureAwait(false);
                    if (_boundAgentRun?.AgentRunId == run.AgentRunId) _boundAgentRun = run;
                }
            }
            else if (!run.IsTerminal)
            {
                if (_boundAgentRun?.AgentRunId == run.AgentRunId && _activeResponseId == run.ResponseId)
                {
                    _boundAgentRun = run;
                    await HandleCancelAsync(new(input.Context, run.ResponseId!.Value), ct).ConfigureAwait(false);
                }
                else
                {
                    run = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(
                        run.Revision, _time.GetUtcNow(), run.KnownEffectSummary), ct).ConfigureAwait(false);
                    if (!run.IsTerminal && run.Claim is { } claim)
                        run = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.CommitCancellation(
                            run.Revision, _time.GetUtcNow(), claim.Generation, run.KnownEffectSummary), ct).ConfigureAwait(false);
                    if (_boundAgentRun?.AgentRunId == run.AgentRunId) _boundAgentRun = run.IsTerminal ? null : run;
                }
            }
            input.Completed.TrySetResult((await _agentRuns.GetAsync(RunOwner, input.RunId, ct).ConfigureAwait(false))!);
        }
        catch (Exception exception) { input.Completed.TrySetException(exception); }
    }
}
