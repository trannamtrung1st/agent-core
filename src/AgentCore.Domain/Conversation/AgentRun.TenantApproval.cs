namespace AgentCore.Domain.Conversation;

public sealed partial class AgentRun
{
    public AgentRun ForkApprovalContinuation(Guid id, AgentRunAdmission admission, Guid requestId, DateTimeOffset at)
    {
        if (Status != AgentRunStatus.WaitingForApproval || CancellationRequested || Owner.TenantId is null || ApprovalRequestId != requestId || AttemptCount == 0
            || admission.Activation.Kind != ActivationKind.ApprovalContinuation || admission.Activation.SourceAgentRunId != AgentRunId
            || admission.ApprovalResume?.RequestId != requestId || admission.ResponseId != ResponseId || Checkpoint is null
            || SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "An exact saved approval checkpoint is required for continuation.");
        return new(id, Owner, admission, PinnedModel, AgentRunStatus.Queued, 1, AttemptCount, MaxAttempts, null, null, false, null,
            KnownEffectSummary, Progress, Checkpoint, null, null, SideEffect, null, at, at, PinnedSkillCatalog, ActiveSkillKeys, SkillLoadCount,
            LoadedCapabilityIds, CapabilityLoadCount, null, WaitCount, TotalWaitSeconds, requestId);
    }

    public AgentRun DeferUnacceptedApprovalDispatch(long revision, Guid generation, DateTimeOffset at, DateTimeOffset retryAt)
    {
        RequireOperational(revision, generation);
        AgentRunTime.RequireUtc(retryAt, "Approval dispatch retry");
        if (Admission.ApprovalResume is null || Admission.Activation.Kind != ActivationKind.ApprovalContinuation || Owner.TenantId is null || ApprovalRequestId is null || AttemptCount == 0
            || Checkpoint is null || retryAt <= at || SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Only a known unaccepted approval continuation can defer with its checkpoint.");
        var checkpoint = Checkpoint.PauseBudgetClock(Claim!.ClaimedAtUtc).WithRuntimeAdmissionPending(true);
        return Copy(AgentRunStatus.WaitingToRetry, checked(Revision + 1), AttemptCount, retryAt, null, false, null, KnownEffectSummary,
            Progress, checkpoint, Result, Failure, SideEffect, Approval, at);
    }

    public AgentRun HandoffApprovalContinuation(long revision, Guid requestId, Guid nextRunId, DateTimeOffset at)
    {
        RequireRevision(revision);
        if (Status != AgentRunStatus.WaitingForApproval || CancellationRequested || ApprovalRequestId != requestId || nextRunId == AgentRunId)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Approval handoff requires its waiting source.");
        var result = new AgentRunResult("Exact approval operation continues in the linked Run.", at, outcomeKind: AgentRunOutcomeKind.Continued, continuationRunId: nextRunId);
        return Copy(AgentRunStatus.Completed, checked(Revision + 1), AttemptCount, null, null, false, null, KnownEffectSummary,
            Progress, Checkpoint, result, null, SideEffect, null, at);
    }

    public AgentRun RecordDirectLocalEffect(long revision, Guid generation, Guid operationId, string actionHash, DateTimeOffset at)
    {
        RequireOperational(revision, generation);
        if (Owner.TenantId is null || operationId == Guid.Empty || Claim!.LeaseExpiresAtUtc <= at
            || SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "A live direct local operation is required.");
        return Copy(Status, checked(Revision + 1), AttemptCount, NextRetryAtUtc, Claim, false, null, KnownEffectSummary,
            Progress, Checkpoint, Result, Failure, new(AgentRunSideEffectDisposition.Succeeded, "operation:" + operationId.ToString("D"), actionHash, at), Approval, at);
    }
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
