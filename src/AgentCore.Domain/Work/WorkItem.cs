namespace AgentCore.Domain.Work;

public sealed class WorkItem
{
    public WorkItem(
        Guid workItemId,
        WorkOwner owner,
        WorkProvenance provenance,
        WorkModelPin model,
        WorkItemStatus status,
        long revision,
        int attemptCount,
        int maxAttempts,
        DateTimeOffset? nextRetryAtUtc,
        WorkClaim? claim,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        string? knownEffectSummary,
        WorkProgress? progress,
        WorkCheckpoint? checkpoint,
        WorkResult? result,
        WorkFailure? failure,
        WorkSideEffect sideEffect,
        WorkApproval? approval,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (workItemId == Guid.Empty)
        {
            throw new ArgumentException("Work item identifier is required.", nameof(workItemId));
        }

        if (provenance is null)
        {
            throw new ArgumentException("Provenance is required.", nameof(provenance));
        }

        if (model is null)
        {
            throw new ArgumentException("Model pin is required.", nameof(model));
        }

        if (sideEffect is null)
        {
            throw new ArgumentException("Side effect is required.", nameof(sideEffect));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Work item status is not valid.", nameof(status));
        }

        if (revision < 1)
        {
            throw new ArgumentException("Work item revision starts at 1.");
        }

        if (maxAttempts is < WorkLimits.MinMaxAttempts or > WorkLimits.MaxMaxAttempts)
        {
            throw new ArgumentException("Retry budget must be between 1 and 8.");
        }

        if (attemptCount < 0 || attemptCount > maxAttempts)
        {
            throw new ArgumentException("Attempt count is outside the retry budget.");
        }

        WorkTime.RequireUtc(createdAtUtc, "Created");
        WorkTime.RequireUtc(updatedAtUtc, "Updated");
        WorkTime.RequireUtc(nextRetryAtUtc, "Retry");
        WorkTime.RequireUtc(cancellationRequestedAtUtc, "Cancellation");
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Updated time cannot precede creation.");
        }

        var terminal = status is WorkItemStatus.Completed or WorkItemStatus.Failed or WorkItemStatus.Cancelled;
        if (status == WorkItemStatus.Running)
        {
            if (claim is null)
            {
                throw new ArgumentException("Running work requires an execution claim.");
            }
        }
        else if (claim is not null)
        {
            throw new ArgumentException("Only running work may hold an execution claim.");
        }

        if (status == WorkItemStatus.Completed)
        {
            if (result is null || failure is not null)
            {
                throw new ArgumentException("Completed work requires a result and no failure.");
            }
        }
        else if (status == WorkItemStatus.Failed)
        {
            if (failure is null || result is not null)
            {
                throw new ArgumentException("Failed work requires a failure and no result.");
            }
        }
        else if (result is not null || failure is not null)
        {
            throw new ArgumentException("Only terminal success or failure may store a result boundary.");
        }

        if (status == WorkItemStatus.Cancelled && !cancellationRequested)
        {
            throw new ArgumentException("Cancelled work must record the cancellation request.");
        }

        if (status is WorkItemStatus.Completed or WorkItemStatus.Failed && cancellationRequested)
        {
            throw new ArgumentException("Completed or failed work cannot also be cancelled.");
        }

        if (cancellationRequested != cancellationRequestedAtUtc.HasValue)
        {
            throw new ArgumentException("Cancellation time must match the cancellation request.");
        }

        if ((status == WorkItemStatus.WaitingToRetry) != nextRetryAtUtc.HasValue)
        {
            throw new ArgumentException("Retry time is required only while waiting to retry.");
        }

        if (approval is not null && approval.WorkItemId != workItemId)
        {
            throw new ArgumentException("Approval belongs to a different work item.");
        }

        var pending = approval is { Decision: WorkApprovalDecision.Pending };
        if ((status == WorkItemStatus.WaitingForApproval) != pending)
        {
            throw new ArgumentException("Waiting for approval requires one pending approval.");
        }

        if (sideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate
            && status is not (WorkItemStatus.Running or WorkItemStatus.Failed or WorkItemStatus.Cancelled))
        {
            throw new ArgumentException("An uncertain side effect cannot return to a runnable state.");
        }

        if (knownEffectSummary is not null && !cancellationRequested)
        {
            throw new ArgumentException("A known effect belongs to a cancellation.");
        }

        WorkItemId = workItemId;
        Owner = new WorkOwner(owner.AgentInstanceId, owner.ProfileId);
        Provenance = provenance;
        Model = model;
        Status = status;
        Revision = revision;
        AttemptCount = attemptCount;
        MaxAttempts = maxAttempts;
        NextRetryAtUtc = nextRetryAtUtc;
        Claim = claim;
        CancellationRequested = cancellationRequested;
        CancellationRequestedAtUtc = cancellationRequestedAtUtc;
        KnownEffectSummary = WorkText.OptionalLine(knownEffectSummary, WorkLimits.MaxKnownEffectCharacters, "Known effect");
        Progress = progress;
        Checkpoint = checkpoint;
        Result = result;
        Failure = failure;
        SideEffect = sideEffect;
        Approval = approval;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid WorkItemId { get; }

    public WorkOwner Owner { get; }

    public WorkProvenance Provenance { get; }

    public WorkModelPin Model { get; }

    public WorkItemStatus Status { get; }

    public long Revision { get; }

    public int AttemptCount { get; }

    public int MaxAttempts { get; }

    public DateTimeOffset? NextRetryAtUtc { get; }

    public WorkClaim? Claim { get; }

    public bool CancellationRequested { get; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; }

    public string? KnownEffectSummary { get; }

    public WorkProgress? Progress { get; }

    public WorkCheckpoint? Checkpoint { get; }

    public WorkResult? Result { get; }

    public WorkFailure? Failure { get; }

    public WorkSideEffect SideEffect { get; }

    public WorkApproval? Approval { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public bool IsTerminal =>
        Status is WorkItemStatus.Completed or WorkItemStatus.Failed or WorkItemStatus.Cancelled;

    public bool HasLiveClaim => Claim is not null;

    public bool IsInitialQueued =>
        Status == WorkItemStatus.Queued
        && Revision == 1
        && AttemptCount == 0
        && Claim is null
        && !CancellationRequested
        && NextRetryAtUtc is null
        && Progress is null
        && Checkpoint is null
        && Result is null
        && Failure is null
        && Approval is null
        && SideEffect.Disposition == WorkSideEffectDisposition.None
        && KnownEffectSummary is null;

    public string OriginLabel => Provenance.SourceKind switch
    {
        WorkSourceKind.Schedule => "Scheduled reminder",
        WorkSourceKind.ApplicationEvent => "Application event",
        _ => throw new InvalidOperationException("Source kind is not valid.")
    };

    public static WorkItem Create(
        Guid workItemId,
        WorkOwner owner,
        WorkProvenance provenance,
        WorkModelPin model,
        int maxAttempts,
        DateTimeOffset createdAtUtc) =>
        new(
            workItemId,
            owner,
            provenance,
            model,
            WorkItemStatus.Queued,
            revision: 1,
            attemptCount: 0,
            maxAttempts,
            nextRetryAtUtc: null,
            claim: null,
            cancellationRequested: false,
            cancellationRequestedAtUtc: null,
            knownEffectSummary: null,
            progress: null,
            checkpoint: null,
            result: null,
            failure: null,
            WorkSideEffect.None,
            approval: null,
            createdAtUtc,
            createdAtUtc);

    public WorkItem TakeClaim(Guid generation, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        var resumeSameAttempt = IsApprovalResume;
        if (CancellationRequested
            || (!resumeSameAttempt && AttemptCount >= MaxAttempts)
            || Status is not (WorkItemStatus.Queued or WorkItemStatus.WaitingToRetry)
            || (Status == WorkItemStatus.WaitingToRetry && NextRetryAtUtc > claimedAtUtc))
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.NotClaimable, "Work item cannot be claimed.");
        }

        var claim = new WorkClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
        return Copy(
            WorkItemStatus.Running,
            Revision + 1,
            resumeSameAttempt ? AttemptCount : AttemptCount + 1,
            nextRetryAtUtc: null,
            claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffect,
            Approval,
            claimedAtUtc);
    }

    public WorkItem RenewClaim(long expectedRevision, Guid generation, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset renewedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        if (leaseExpiresAtUtc <= renewedAtUtc)
        {
            throw new ArgumentException("Claim lease must be later than the renewal time.");
        }

        var claim = new WorkClaim(generation, Claim!.ClaimedAtUtc, leaseExpiresAtUtc);
        return Copy(
            Status,
            Revision + 1,
            AttemptCount,
            NextRetryAtUtc,
            claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffect,
            Approval,
            renewedAtUtc);
    }

    public WorkItem SaveCheckpoint(
        long expectedRevision,
        Guid generation,
        WorkCheckpoint checkpoint,
        string? progressSummary,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireOperational(expectedRevision, generation);
        var progress = progressSummary is null ? Progress : new WorkProgress(progressSummary, updatedAtUtc);
        return Copy(
            Status,
            Revision + 1,
            AttemptCount,
            NextRetryAtUtc,
            Claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            progress,
            checkpoint,
            Result,
            Failure,
            SideEffect,
            Approval,
            updatedAtUtc);
    }

    public WorkItem Complete(long expectedRevision, Guid generation, string resultText, DateTimeOffset completedAtUtc)
    {
        if (Status == WorkItemStatus.Completed
            && resultText is not null
            && string.Equals(Result!.Text, resultText.Trim(), StringComparison.Ordinal))
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        if (SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate)
        {
            throw new WorkItemTransitionException(
                WorkTransitionFailure.Rejected,
                "An uncertain external effect cannot be completed.");
        }

        if (resultText is null)
        {
            throw new ArgumentException("Result is required.");
        }

        return Copy(
            WorkItemStatus.Completed,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            cancellationRequested: false,
            cancellationRequestedAtUtc: null,
            knownEffectSummary: null,
            Progress,
            Checkpoint,
            new WorkResult(resultText, completedAtUtc),
            failure: null,
            SideEffect,
            Approval,
            completedAtUtc);
    }

    public WorkItem Fail(
        long expectedRevision,
        Guid generation,
        string failureCode,
        string failureSummary,
        bool replaySafe,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextRetryAtUtc)
    {
        if (Status == WorkItemStatus.Failed
            && failureCode is not null
            && failureSummary is not null
            && Failure!.Code == failureCode.Trim()
            && Failure.Summary == failureSummary.Trim())
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        var unsafeEffect = SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate;
        if (!replaySafe || unsafeEffect || AttemptCount >= MaxAttempts)
        {
            var effect = unsafeEffect ? AsIndeterminate(failedAtUtc) : SideEffect;
            var code = unsafeEffect ? "side-effect-indeterminate" : failureCode ?? throw new ArgumentException("Failure code is required.");
            var summary = unsafeEffect
                ? "External effect outcome is unknown and was not replayed."
                : failureSummary ?? throw new ArgumentException("Failure is required.");
            return AsFailed(failedAtUtc, code, summary, effect);
        }

        if (nextRetryAtUtc is null || nextRetryAtUtc < failedAtUtc)
        {
            throw new ArgumentException("Retry time is required and cannot precede the failure.");
        }

        return Copy(
            WorkItemStatus.WaitingToRetry,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc,
            claim: null,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffect,
            Approval,
            failedAtUtc);
    }

    public WorkItem RequestCancellation(long expectedRevision, string? knownEffectSummary, DateTimeOffset requestedAtUtc)
    {
        if (Status == WorkItemStatus.Cancelled)
        {
            return this;
        }

        if (IsTerminal)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Terminal, "Terminal work cannot be cancelled.");
        }

        RequireRevision(expectedRevision);
        if (Status == WorkItemStatus.Running)
        {
            return Copy(
                Status,
                Revision + 1,
                AttemptCount,
                NextRetryAtUtc,
                Claim,
                cancellationRequested: true,
                requestedAtUtc,
                knownEffectSummary ?? KnownEffectSummary,
                Progress,
                Checkpoint,
                Result,
                Failure,
                SideEffect,
                Approval,
                requestedAtUtc);
        }

        return AsCancelled(requestedAtUtc, knownEffectSummary ?? KnownEffectSummary);
    }

    public WorkItem CommitCancellation(long expectedRevision, Guid generation, string? knownEffectSummary, DateTimeOffset cancelledAtUtc)
    {
        if (Status == WorkItemStatus.Cancelled)
        {
            return this;
        }

        RequireRevision(expectedRevision);
        if (IsTerminal)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Terminal, "Terminal work cannot be cancelled.");
        }

        if (Status != WorkItemStatus.Running || Claim is null || Claim.Generation != generation)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleGeneration, "Work item execution generation is stale.");
        }

        if (!CancellationRequested)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Cancellation was not requested.");
        }

        return AsCancelled(cancelledAtUtc, knownEffectSummary ?? KnownEffectSummary);
    }

    public WorkItem RecoverExpiredClaim(DateTimeOffset asOfUtc)
    {
        WorkTime.RequireUtc(asOfUtc, "Recovery");
        if (Status != WorkItemStatus.Running || Claim is null || Claim.LeaseExpiresAtUtc > asOfUtc)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.NotClaimable, "Work item claim is still current.");
        }

        if (CancellationRequested)
        {
            return AsCancelled(asOfUtc, KnownEffectSummary);
        }

        if (SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate)
        {
            return AsFailed(
                asOfUtc,
                "side-effect-indeterminate",
                "External effect outcome is unknown and was not replayed.",
                AsIndeterminate(asOfUtc));
        }

        if (AttemptCount >= MaxAttempts)
        {
            return AsFailed(asOfUtc, "attempts-exhausted", "Retry budget is exhausted.", SideEffect);
        }

        return Copy(
            WorkItemStatus.WaitingToRetry,
            Revision + 1,
            AttemptCount,
            asOfUtc,
            claim: null,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffect,
            Approval,
            asOfUtc);
    }

    public WorkItem BeginApproval(
        long expectedRevision,
        Guid generation,
        Guid approvalId,
        string toolName,
        string preparedActionJson,
        string actionHash,
        string preview,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset updatedAtUtc)
    {
        if (Status == WorkItemStatus.WaitingForApproval
            && Approval!.ApprovalId == approvalId
            && string.Equals(Approval.ActionHash, actionHash, StringComparison.Ordinal))
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        if (SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate)
        {
            throw new WorkItemTransitionException(
                WorkTransitionFailure.Rejected,
                "An uncertain external effect cannot wait for approval.");
        }

        if (Approval is { Decision: WorkApprovalDecision.Pending })
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Approval is already pending.");
        }

        var approval = new WorkApproval(
            approvalId,
            WorkItemId,
            generation,
            Revision,
            toolName,
            preparedActionJson,
            actionHash,
            preview,
            expiresAtUtc,
            WorkApprovalDecision.Pending,
            decidedAtUtc: null,
            consumed: false,
            revision: 1,
            updatedAtUtc);
        return Copy(
            WorkItemStatus.WaitingForApproval,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffect,
            approval,
            updatedAtUtc);
    }

    public WorkItem DecideApproval(
        Guid approvalId,
        long expectedRevision,
        long expectedApprovalRevision,
        string actionHash,
        WorkApprovalDecision decision,
        DateTimeOffset decidedAtUtc)
    {
        if (decision is not (WorkApprovalDecision.Approved or WorkApprovalDecision.Rejected))
        {
            throw new ArgumentException("Approval can only be approved or rejected.", nameof(decision));
        }

        var approval = RequireApproval(approvalId, actionHash);
        if (approval.Decision == decision)
        {
            return this;
        }

        if (approval.Decision != WorkApprovalDecision.Pending || Status != WorkItemStatus.WaitingForApproval)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.ApprovalMismatch, "Approval was already decided.");
        }

        RequireRevision(expectedRevision);
        if (approval.Revision != expectedApprovalRevision)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleRevision, "Approval revision is stale.");
        }

        if (decidedAtUtc >= approval.ExpiresAtUtc)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.ApprovalMismatch, "Approval has expired.");
        }

        return Copy(
            WorkItemStatus.Queued,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffectForDecision(decision, approval.ActionHash),
            approval.WithDecision(decision, decidedAtUtc, approval.Revision + 1),
            decidedAtUtc);
    }

    public WorkItem ExpireApproval(long expectedRevision, DateTimeOffset expiredAtUtc)
    {
        if (Approval is { Decision: WorkApprovalDecision.Expired } && Status == WorkItemStatus.Queued)
        {
            return this;
        }

        RequireRevision(expectedRevision);
        if (Status != WorkItemStatus.WaitingForApproval || Approval is not { Decision: WorkApprovalDecision.Pending })
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Work item is not waiting for approval.");
        }

        if (expiredAtUtc < Approval.ExpiresAtUtc)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Approval has not expired.");
        }

        return Copy(
            WorkItemStatus.Queued,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            SideEffectForDecision(WorkApprovalDecision.Expired, Approval.ActionHash),
            Approval.WithDecision(WorkApprovalDecision.Expired, expiredAtUtc, Approval.Revision + 1),
            expiredAtUtc);
    }

    public WorkItem MarkSideEffect(
        long expectedRevision,
        Guid generation,
        WorkSideEffectDisposition disposition,
        string actionHash,
        DateTimeOffset updatedAtUtc)
    {
        if (SideEffect.Disposition == disposition && string.Equals(SideEffect.ActionHash, actionHash, StringComparison.Ordinal))
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        if (SideEffect.ActionHash is not null
            && !string.Equals(SideEffect.ActionHash, actionHash, StringComparison.Ordinal))
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Rejected, "Side-effect action does not match.");
        }

        if (disposition is WorkSideEffectDisposition.Prepared or WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Succeeded)
        {
            RequireApprovedDispatch(actionHash);
        }

        if (!CanTransitionSideEffect(SideEffect.Disposition, disposition))
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Illegal, "Side-effect transition is not allowed.");
        }

        return Copy(
            Status,
            Revision + 1,
            AttemptCount,
            NextRetryAtUtc,
            Claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            new WorkSideEffect(disposition, actionHash, updatedAtUtc),
            Approval,
            updatedAtUtc);
    }

    public WorkItemPublicSummary ToPublicSummary() =>
        new(
            WorkItemId,
            Owner.AgentInstanceId,
            Owner.ProfileId,
            Status,
            Revision,
            Provenance.SourceKind,
            Provenance.SourceOccurrenceId,
            OriginLabel,
            Progress?.Summary,
            Status == WorkItemStatus.WaitingForApproval,
            Status == WorkItemStatus.WaitingForApproval ? Approval?.Preview : null,
            Result?.Text,
            Failure?.Code,
            Failure?.Summary,
            KnownEffectSummary,
            CancellationAvailable: !IsTerminal,
            CreatedAtUtc,
            UpdatedAtUtc);

    private WorkItem AsCancelled(DateTimeOffset cancelledAtUtc, string? knownEffectSummary)
    {
        var approval = Approval is { Decision: WorkApprovalDecision.Pending }
            ? Approval.WithDecision(WorkApprovalDecision.Cancelled, cancelledAtUtc, Approval.Revision + 1)
            : Approval;
        var sideEffect = approval != Approval
            ? SideEffectForDecision(WorkApprovalDecision.Cancelled, Approval?.ActionHash)
            : SideEffect;
        return Copy(
            WorkItemStatus.Cancelled,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            cancellationRequested: true,
            CancellationRequestedAtUtc ?? cancelledAtUtc,
            knownEffectSummary,
            Progress,
            Checkpoint,
            Result,
            Failure,
            sideEffect,
            approval,
            cancelledAtUtc);
    }

    private WorkItem AsFailed(DateTimeOffset failedAtUtc, string code, string summary, WorkSideEffect sideEffect) =>
        Copy(
            WorkItemStatus.Failed,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            cancellationRequested: false,
            cancellationRequestedAtUtc: null,
            knownEffectSummary: null,
            Progress,
            Checkpoint,
            result: null,
            new WorkFailure(code, summary, failedAtUtc),
            sideEffect,
            Approval,
            failedAtUtc);

    private WorkSideEffect AsIndeterminate(DateTimeOffset updatedAtUtc)
    {
        if (SideEffect.Disposition == WorkSideEffectDisposition.Indeterminate)
        {
            return SideEffect;
        }

        return new WorkSideEffect(WorkSideEffectDisposition.Indeterminate, SideEffect.ActionHash, updatedAtUtc);
    }

    private void RequireOperational(long expectedRevision, Guid generation)
    {
        RequireRevision(expectedRevision);
        if (IsTerminal)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Terminal, "Terminal work cannot change.");
        }

        if (Status != WorkItemStatus.Running || Claim is null || Claim.Generation != generation)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleGeneration, "Work item execution generation is stale.");
        }

        if (CancellationRequested)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.Rejected, "Cancellation was already requested.");
        }
    }

    private void RequireRevision(long expectedRevision)
    {
        if (Revision != expectedRevision)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.StaleRevision, "Work item revision is stale.");
        }
    }

    private WorkApproval RequireApproval(Guid approvalId, string actionHash)
    {
        if (Approval is null || Approval.ApprovalId != approvalId)
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.ApprovalMismatch, "Approval was not found.");
        }

        if (!string.Equals(Approval.ActionHash, actionHash, StringComparison.Ordinal))
        {
            throw new WorkItemTransitionException(WorkTransitionFailure.ApprovalMismatch, "Approval action does not match.");
        }

        return Approval;
    }

    private bool IsApprovalResume =>
        Status == WorkItemStatus.Queued
        && Approval is
        {
            Decision: WorkApprovalDecision.Approved or WorkApprovalDecision.Rejected or WorkApprovalDecision.Expired
        };

    private WorkSideEffect SideEffectForDecision(WorkApprovalDecision decision, string? actionHash)
    {
        if (SideEffect.Disposition != WorkSideEffectDisposition.Prepared)
        {
            return SideEffect;
        }

        if (decision == WorkApprovalDecision.Approved
            && string.Equals(SideEffect.ActionHash, actionHash, StringComparison.Ordinal))
        {
            return SideEffect;
        }

        if (decision == WorkApprovalDecision.Approved)
        {
            throw new WorkItemTransitionException(
                WorkTransitionFailure.ApprovalMismatch,
                "Prepared action does not match the approval.");
        }

        return WorkSideEffect.None;
    }

    private void RequireApprovedDispatch(string actionHash)
    {
        if (Approval is null)
        {
            return;
        }

        if (Approval.Decision != WorkApprovalDecision.Approved
            || !string.Equals(Approval.ActionHash, actionHash, StringComparison.Ordinal))
        {
            throw new WorkItemTransitionException(
                WorkTransitionFailure.Rejected,
                "Side effect requires the approved action.");
        }
    }

    private static bool CanTransitionSideEffect(WorkSideEffectDisposition from, WorkSideEffectDisposition to) =>
        (from, to) switch
        {
            (WorkSideEffectDisposition.None, WorkSideEffectDisposition.Prepared) => true,
            (WorkSideEffectDisposition.Prepared, WorkSideEffectDisposition.InFlight) => true,
            (WorkSideEffectDisposition.Prepared, WorkSideEffectDisposition.DefinitelyFailed) => true,
            (WorkSideEffectDisposition.InFlight, WorkSideEffectDisposition.Succeeded) => true,
            (WorkSideEffectDisposition.InFlight, WorkSideEffectDisposition.DefinitelyFailed) => true,
            (WorkSideEffectDisposition.InFlight, WorkSideEffectDisposition.Indeterminate) => true,
            _ => false
        };

    private WorkItem Copy(
        WorkItemStatus status,
        long revision,
        int attemptCount,
        DateTimeOffset? nextRetryAtUtc,
        WorkClaim? claim,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        string? knownEffectSummary,
        WorkProgress? progress,
        WorkCheckpoint? checkpoint,
        WorkResult? result,
        WorkFailure? failure,
        WorkSideEffect sideEffect,
        WorkApproval? approval,
        DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < UpdatedAtUtc)
        {
            throw new ArgumentException("Updated time cannot move backwards.");
        }

        return new(
            WorkItemId,
            Owner,
            Provenance,
            Model,
            status,
            revision,
            attemptCount,
            MaxAttempts,
            nextRetryAtUtc,
            claim,
            cancellationRequested,
            cancellationRequestedAtUtc,
            knownEffectSummary,
            progress,
            checkpoint,
            result,
            failure,
            sideEffect,
            approval,
            CreatedAtUtc,
            updatedAtUtc);
    }
}
