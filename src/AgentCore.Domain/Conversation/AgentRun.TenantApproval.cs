namespace AgentCore.Domain.Conversation;

public sealed partial class AgentRun
{
    public AgentRun RecordApprovedLocalEffect(long revision, Guid generation, Guid requestId, Guid operationId,
        string actionHash, bool succeeded, DateTimeOffset at)
    {
        RequireOperational(revision, generation);
        if (Owner.TenantId is null || ApprovalRequestId != requestId || requestId == Guid.Empty || operationId == Guid.Empty
            || Claim!.LeaseExpiresAtUtc <= at || SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "A live exact tenant operation is required.");
        if (SideEffect.Disposition == AgentRunSideEffectDisposition.Prepared && SideEffect.ActionHash != actionHash)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Another side effect is prepared.");
        return Copy(Status, checked(Revision + 1), AttemptCount, NextRetryAtUtc, Claim, false, null, KnownEffectSummary,
            Progress, Checkpoint, Result, Failure, new(succeeded ? AgentRunSideEffectDisposition.Succeeded : AgentRunSideEffectDisposition.DefinitelyFailed,
                "operation:" + operationId.ToString("D"), actionHash, at), null, at);
    }

    // A queued operation may require authorization before its first model attempt. Running work is generation-fenced.
    public AgentRun WaitForDurableApproval(long revision, Guid requestId, Guid? generation, DateTimeOffset at)
    {
        RequireRevision(revision);
        if (Owner.TenantId is null || requestId == Guid.Empty || CancellationRequested
            || Status is not (AgentRunStatus.Queued or AgentRunStatus.Running)
            || Status == AgentRunStatus.Queued && generation is not null
            || SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Run cannot wait for tenant approval.");
        if (Status == AgentRunStatus.Running)
        {
            RequireOperational(revision, generation ?? Guid.Empty);
            if (Claim!.LeaseExpiresAtUtc <= at) throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleGeneration, "Execution claim has expired.");
        }
        return Copy(AgentRunStatus.WaitingForApproval, checked(Revision + 1), AttemptCount, null, null,
            CancellationRequested, CancellationRequestedAtUtc, KnownEffectSummary, Progress, Checkpoint, Result, Failure, SideEffect, null, at,
            approvalRequestId: requestId);
    }

    public AgentRun ResumeDurableApproval(long revision, Guid requestId, DateTimeOffset at)
    {
        RequireRevision(revision);
        if (Owner.TenantId is null || Status != AgentRunStatus.WaitingForApproval || ApprovalRequestId != requestId || CancellationRequested)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Approval does not own the waiting Run.");
        return Copy(AgentRunStatus.Queued, checked(Revision + 1), AttemptCount, null, null,
            false, null, KnownEffectSummary, Progress, Checkpoint, Result, Failure, SideEffect, null, at);
    }
}
