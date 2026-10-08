using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Execution;

/// <summary>One claim/recovery path for both immediate admission and scheduler dispatch.</summary>
public sealed class AgentRunCoordinator(IAgentRunStore store, IAgentRunDispatcher dispatcher,
    IIdGenerator ids, TimeProvider time, BackgroundCompletionReporter? reports = null)
{
    public const int DefaultBatchSize = 8;
    public static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(5);

    public async ValueTask<bool> DispatchAsync(AgentRunOwner owner, Guid agentRunId,
        CancellationToken cancellationToken = default)
    {
        var run = await store.GetAsync(owner, agentRunId, cancellationToken).ConfigureAwait(false);
        return run is not null && await PrepareAndDispatchAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<int> ExecuteRunnableAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (reports is not null) await reports.ReportPendingAsync(limit, cancellationToken).ConfigureAwait(false);
        var pendingSessions = await store.ListPendingInputSessionsAsync(limit, cancellationToken).ConfigureAwait(false);
        foreach (var sessionId in pendingSessions)
            await dispatcher.RepairPendingInputsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var due = await store.ListRunnableAsync(time.GetUtcNow(), limit, cancellationToken).ConfigureAwait(false);
        var dispatched = 0;
        foreach (var run in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await PrepareAndDispatchAsync(run, cancellationToken).ConfigureAwait(false)) dispatched++;
        }
        return dispatched;
    }

    private async ValueTask<bool> PrepareAndDispatchAsync(AgentRun run, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        try
        {
            if (run.Status == AgentRunStatus.WaitingForSignal)
                run = await store.ApplyAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.ResumeWait(run.Revision, now, ids.NewId(), now + ClaimDuration), cancellationToken).ConfigureAwait(false);
            else if (run.Status == AgentRunStatus.Running)
            {
                if (run.Claim!.LeaseExpiresAtUtc > now) return false;
                run = await store.ApplyAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.Recover(run.Revision, now), cancellationToken).ConfigureAwait(false);
            }
            else if (run.Status == AgentRunStatus.WaitingForApproval)
            {
                if (run.Approval!.ExpiresAtUtc > now) return false;
                run = await store.ApplyAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.ExpireApproval(run.Revision, now), cancellationToken).ConfigureAwait(false);
            }

            if (run.Status is AgentRunStatus.Queued or AgentRunStatus.WaitingToRetry)
            {
                if (run.NextRetryAtUtc > now) return false;
                run = await store.ApplyAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.Claim(run.Revision, now, ids.NewId(), now + ClaimDuration),
                    cancellationToken).ConfigureAwait(false);
            }
            // Browser recovery can return a fenced observation-only running claim directly.
            if (run.Status != AgentRunStatus.Running) return false;
        }
        catch (AgentCoreException exception) when (exception.Code is "Conflict" or "NotFound")
        {
            // A competing fast path/worker won the CAS, or the run was removed. It owns dispatch.
            return false;
        }

        // Leave unexpected/transport failures claimed for lease recovery. They may have reached the mailbox;
        // treating them as replay-safe or immediately releasing the claim could duplicate model/tool work.
        try
        {
            var accepted = await dispatcher.DispatchAsync(run, cancellationToken).ConfigureAwait(false);
            if (!accepted && run.Checkpoint is null && run.SideEffect.Disposition == AgentRunSideEffectDisposition.None)
            {
                try
                {
                    await store.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.DeferDispatch(run.Revision,
                        time.GetUtcNow(), run.Claim!.Generation, time.GetUtcNow().AddSeconds(5)), cancellationToken).ConfigureAwait(false);
                }
                catch (AgentCoreException conflict) when (conflict.Code is "Conflict" or "NotFound") { }
            }
            return accepted;
        }
        catch (AgentCoreException exception) when (exception.Code == "NotFound"
            && exception.Message.Contains("Session was not found", StringComparison.Ordinal))
        {
            var current = await store.GetAsync(run.Owner, run.AgentRunId, cancellationToken).ConfigureAwait(false);
            if (current is { Status: AgentRunStatus.Running, Claim: { } claim }
                && claim.Generation == run.Claim!.Generation)
            {
                try
                {
                    await store.ApplyAsync(current.Owner, current.AgentRunId,
                        new AgentRunCommand.Fail(current.Revision, time.GetUtcNow(), claim.Generation,
                            "session-unavailable", "Session is no longer available.", false, null),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AgentCoreException conflict) when (conflict.Code is "Conflict" or "NotFound")
                {
                    // Concurrent cancellation/recovery owns the newer state.
                }
            }
            return false;
        }
    }
}
