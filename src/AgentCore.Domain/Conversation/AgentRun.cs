using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Conversation;

public sealed partial class AgentRun
{
    public AgentRun(
        Guid agentRunId,
        AgentRunOwner owner,
        AgentRunAdmission admission,
        AgentRunModelPin pinnedModel,
        AgentRunStatus status,
        long revision,
        int attemptCount,
        int maxAttempts,
        DateTimeOffset? nextRetryAtUtc,
        AgentRunClaim? claim,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        string? knownEffectSummary,
        AgentRunProgress? progress,
        AgentRunCheckpoint? checkpoint,
        AgentRunResult? result,
        AgentRunFailure? failure,
        AgentRunSideEffect sideEffect,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<EffectiveSkill>? pinnedSkillCatalog = null,
        IReadOnlyList<string>? activeSkillKeys = null,
        int skillLoadCount = 0,
        IReadOnlyList<string>? loadedCapabilityIds = null,
        int capabilityLoadCount = 0,
        AgentRunWait? wait = null, int waitCount = 0, double totalWaitSeconds = 0, Guid? approvalRequestId = null)
    {
        if (agentRunId == Guid.Empty)
        {
            throw new ArgumentException("Agent run identifier is required.", nameof(agentRunId));
        }

        if (admission is null)
        {
            throw new ArgumentException("Admission is required.", nameof(admission));
        }

        if (pinnedModel is null)
        {
            throw new ArgumentException("Model pin is required.", nameof(pinnedModel));
        }
        if (pinnedModel.Binding is { } binding && owner.TenantId != binding.Route.TenantId)
            throw new ArgumentException("The model route must belong to the admitted Run tenant.", nameof(pinnedModel));

        if (sideEffect is null)
        {
            throw new ArgumentException("Side effect is required.", nameof(sideEffect));
        }

        if (!Enum.IsDefined(status))
        {
            throw new ArgumentException("Agent run status is not valid.", nameof(status));
        }

        if (revision < 1)
        {
            throw new ArgumentException("Agent run revision starts at 1.");
        }

        if (maxAttempts is < AgentRunLimits.MinMaxAttempts or > AgentRunLimits.MaxMaxAttempts)
        {
            throw new ArgumentException("Retry budget must be between 1 and 8.");
        }

        if (attemptCount < 0 || attemptCount > maxAttempts)
        {
            throw new ArgumentException("Attempt count is outside the retry budget.");
        }

        if (createdAtUtc.ToUnixTimeMilliseconds() < admission.Activation.AdmittedAtUtc.ToUnixTimeMilliseconds())
            throw new ArgumentException("Execution cannot precede admission.");
        AgentRunTime.RequireUtc(createdAtUtc, "Created");
        AgentRunTime.RequireUtc(updatedAtUtc, "Updated");
        AgentRunTime.RequireUtc(nextRetryAtUtc, "Retry");
        AgentRunTime.RequireUtc(cancellationRequestedAtUtc, "Cancellation");
        if (updatedAtUtc < createdAtUtc)
        {
            throw new ArgumentException("Updated time cannot precede creation.");
        }

        wait?.Validate();
        if ((status == AgentRunStatus.WaitingForSignal) != (wait is not null) || wait is not null && checkpoint is null
            || wait is not null && (wait.SuspendedGeneration is null || wait.SuspendedGeneration == Guid.Empty)
            || waitCount is < 0 or > AgentRunLimits.MaxWaitCount || !double.IsFinite(totalWaitSeconds)
            || totalWaitSeconds is < 0 or > AgentRunLimits.MaxTotalWaitSeconds)
            throw new ArgumentException("Waiting execution requires a checkpoint and bounded wait accounting.");
        Wait = wait; WaitCount = waitCount; TotalWaitSeconds = totalWaitSeconds;
        var terminal = status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled;
        if (status == AgentRunStatus.Running)
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

        if (status == AgentRunStatus.Completed)
        {
            if (result is null || failure is not null)
            {
                throw new ArgumentException("Completed work requires a result and no failure.");
            }
        }
        else if (status == AgentRunStatus.Failed)
        {
            if (failure is null || result is not null)
            {
                throw new ArgumentException("Failed work requires a failure and no result.");
            }
        }
        else if (result is not null)
        {
            throw new ArgumentException("Only completed work may store a result.");
        }

        if (status == AgentRunStatus.Cancelled && !cancellationRequested)
        {
            throw new ArgumentException("Cancelled work must record the cancellation request.");
        }

        if (status is AgentRunStatus.Completed or AgentRunStatus.Failed && cancellationRequested)
        {
            throw new ArgumentException("Completed or failed work cannot also be cancelled.");
        }

        if (cancellationRequested != cancellationRequestedAtUtc.HasValue)
        {
            throw new ArgumentException("Cancellation time must match the cancellation request.");
        }

        if ((status == AgentRunStatus.WaitingToRetry) != nextRetryAtUtc.HasValue)
        {
            throw new ArgumentException("Retry time is required only while waiting to retry.");
        }

        if (approvalRequestId == Guid.Empty || owner.TenantId is null && approvalRequestId is not null)
            throw new ArgumentException("Approval references require a tenant Run.");
        if (status == AgentRunStatus.WaitingForApproval && (owner.TenantId is null || approvalRequestId is null))
            throw new ArgumentException("Waiting for approval requires a durable tenant request.");

        if (sideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            && status is not (AgentRunStatus.Running or AgentRunStatus.Failed or AgentRunStatus.Cancelled)
            && !(status == AgentRunStatus.WaitingToRetry && ExternalResultAwaitingAdmission(owner, admission, approvalRequestId, checkpoint, sideEffect)))
        {
            throw new ArgumentException("An uncertain side effect cannot return to a runnable state.");
        }

        if (knownEffectSummary is not null
            && !cancellationRequested
            && !AgentRunKnownEffects.IsHistoricalCompletedEffect(knownEffectSummary))
        {
            throw new ArgumentException("A known effect belongs to a cancellation.");
        }

        AgentRunId = agentRunId;
        Owner = new AgentRunOwner(owner.AgentInstanceId, owner.ProfileIdOrNull, owner.TenantId, owner.ExecutorPrincipalId);
        var context = admission.ExecutionContext;
        if (owner.TenantId is null ? context is not null : context is null || context.TenantId != owner.TenantId
            || context.AgentPrincipalId != owner.ExecutorPrincipalId || context.RunId != agentRunId
            || context.SessionId != admission.Activation.SessionId || context.ActivationId != admission.Activation.ActivationId
            || context.RootActivationId != admission.Activation.RootActivationId || context.ParentRunId != admission.Activation.SourceAgentRunId
            || context.ParentActivationId != admission.Activation.ParentActivationId
            || !Enum.TryParse<ActivationKind>(context.Origin switch { ExecutionOrigin.UserMessage => "UserTurn", ExecutionOrigin.CompletionReport => "BackgroundCompleted", ExecutionOrigin.ScheduledOccurrence => "ScheduledWork", ExecutionOrigin.ManualInvocation => "ManualBackground", _ => context.Origin.ToString() }, out var expectedKind)
            || expectedKind != admission.Activation.Kind)
            throw new ArgumentException("Run admission disagrees with trusted execution identity.");
        Admission = admission;
        PinnedModel = pinnedModel;
        Status = status;
        Revision = revision;
        AttemptCount = attemptCount;
        MaxAttempts = maxAttempts;
        NextRetryAtUtc = nextRetryAtUtc;
        Claim = claim;
        CancellationRequested = cancellationRequested;
        CancellationRequestedAtUtc = cancellationRequestedAtUtc;
        KnownEffectSummary = AgentRunText.OptionalLine(knownEffectSummary, AgentRunLimits.MaxKnownEffectCharacters, "Known effect");
        Progress = progress;
        Checkpoint = checkpoint;
        Result = result;
        Failure = failure;
        SideEffect = sideEffect;
        ApprovalRequestId = approvalRequestId;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        PinnedSkillCatalog = SkillPolicy.FreezeCatalog(pinnedSkillCatalog ?? []);
        ActiveSkillKeys = Array.AsReadOnly((activeSkillKeys ?? []).ToArray());
        if (ActiveSkillKeys.Distinct(StringComparer.Ordinal).Count() != ActiveSkillKeys.Count)
            throw new ArgumentException("Active Skill keys must be distinct.");
        SkillPolicy.ValidateActive(PinnedSkillCatalog, ActiveSkillKeys);
        if (skillLoadCount is < 0 or > 2 || capabilityLoadCount is < 0 or > 8
            || (loadedCapabilityIds?.Count ?? 0) > 64
            || AgentDefinitionValidator.ToolAllowlistFindings(loadedCapabilityIds ?? []).Count > 0
            || (loadedCapabilityIds ?? []).Distinct(StringComparer.Ordinal).Count() != (loadedCapabilityIds?.Count ?? 0))
            throw new ArgumentException("Execution projection exceeds bounded discovery limits.");
        SkillLoadCount = skillLoadCount;
        LoadedCapabilityIds = Array.AsReadOnly((loadedCapabilityIds ?? []).ToArray());
        CapabilityLoadCount = capabilityLoadCount;
    }

    public Guid AgentRunId { get; }
    public Guid? ApprovalRequestId { get; }

    public AgentRunOwner Owner { get; }

    public AgentRunAdmission Admission { get; }

    public AgentRunModelPin PinnedModel { get; }

    public AgentRunWait? Wait { get; }
    public int WaitCount { get; }
    public double TotalWaitSeconds { get; }

    public AgentRunStatus Status { get; }

    public long Revision { get; }

    public int AttemptCount { get; }

    public int MaxAttempts { get; }

    public DateTimeOffset? NextRetryAtUtc { get; }

    public AgentRunClaim? Claim { get; }

    public bool CancellationRequested { get; }

    public DateTimeOffset? CancellationRequestedAtUtc { get; }

    public string? KnownEffectSummary { get; }

    public AgentRunProgress? Progress { get; }

    public AgentRunCheckpoint? Checkpoint { get; }

    public AgentRunResult? Result { get; }

    public AgentRunFailure? Failure { get; }

    public AgentRunSideEffect SideEffect { get; }


    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public bool IsTerminal =>
        Status is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled;

    public bool HasLiveClaim => Claim is not null;

    public bool IsInitialQueued =>
        Status == AgentRunStatus.Queued
        && Revision == 1
        && AttemptCount == 0
        && Claim is null
        && !CancellationRequested
        && NextRetryAtUtc is null
        && Progress is null
        && Checkpoint is null
        && Result is null
        && Failure is null
        && SideEffect.Disposition == AgentRunSideEffectDisposition.None
        && KnownEffectSummary is null;

    public Guid SessionId => Admission.Activation.SessionId;
    public Guid ActivationId => Admission.Activation.ActivationId;
    public Guid AgentInstanceId => Owner.AgentInstanceId;
    public Guid ProfileId => Owner.ProfileId;
    public Guid? ResponseId => Admission.ResponseId;
    public string DefinitionId => Admission.DefinitionId;
    public int DefinitionVersion => Admission.DefinitionVersion;
    public AgentIdentity PinnedPersona => Admission.PinnedPersona;
    public IReadOnlyList<EffectiveSkill> PinnedSkillCatalog { get; }
    public IReadOnlyList<string> ActiveSkillKeys { get; }
    public int SkillLoadCount { get; }
    public IReadOnlyList<string> LoadedCapabilityIds { get; }
    public int CapabilityLoadCount { get; }
    public Guid? OutcomeEntryId => Result?.OutcomeEntryId;

    public static AgentRun Create(
        Guid agentRunId,
        AgentRunOwner owner,
        AgentRunAdmission admission,
        AgentRunModelPin pinnedModel,
        int maxAttempts,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<EffectiveSkill>? pinnedSkillCatalog = null,
        IReadOnlyList<string>? activeSkillKeys = null) =>
        new(
            agentRunId,
            owner,
            admission,
            pinnedModel,
            AgentRunStatus.Queued,
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
            AgentRunSideEffect.None,
            createdAtUtc,
            createdAtUtc, pinnedSkillCatalog, activeSkillKeys);

    public AgentRun AdmitActiveSkills(long expectedRevision, Guid generation,
        IReadOnlyList<string> keys, DateTimeOffset updatedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        ArgumentNullException.ThrowIfNull(keys);
        if (SkillLoadCount >= 2)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Skill load budget is exhausted.");
        var combined = ActiveSkillKeys.Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
        SkillPolicy.ValidateActive(PinnedSkillCatalog, combined);
        return CopyProjection(combined, SkillLoadCount + 1, LoadedCapabilityIds, CapabilityLoadCount, updatedAtUtc);
    }

    public AgentRun AdmitCapabilities(long expectedRevision, Guid generation,
        IReadOnlyList<string> names, DateTimeOffset updatedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        ArgumentNullException.ThrowIfNull(names);
        if (CapabilityLoadCount >= 8)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Capability load budget is exhausted.");
        var combined = LoadedCapabilityIds.Concat(names).Distinct(StringComparer.Ordinal).ToArray();
        return CopyProjection(ActiveSkillKeys, SkillLoadCount, combined, CapabilityLoadCount + 1, updatedAtUtc);
    }

    public AgentRun AdmitRecoveryCapability(long expectedRevision, Guid generation, string name, DateTimeOffset updatedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return CopyProjection(ActiveSkillKeys, SkillLoadCount, LoadedCapabilityIds.Append(name).Distinct(StringComparer.Ordinal).ToArray(), CapabilityLoadCount, updatedAtUtc);
    }

    private AgentRun CopyProjection(IReadOnlyList<string> activeSkillKeys, int skillLoadCount,
        IReadOnlyList<string> loadedCapabilityIds, int capabilityLoadCount, DateTimeOffset updatedAtUtc)
    {
        if (updatedAtUtc < UpdatedAtUtc) throw new ArgumentException("Updated time cannot move backwards.");
        return new AgentRun(AgentRunId, Owner, Admission, PinnedModel, Status, Revision + 1, AttemptCount,
            MaxAttempts, NextRetryAtUtc, Claim, CancellationRequested, CancellationRequestedAtUtc,
            KnownEffectSummary, Progress, Checkpoint, Result, Failure, SideEffect, CreatedAtUtc,
            updatedAtUtc, PinnedSkillCatalog, activeSkillKeys, skillLoadCount, loadedCapabilityIds, capabilityLoadCount, Wait, WaitCount, TotalWaitSeconds, ApprovalRequestId);
    }

    public AgentRun SuspendForSignal(long revision, Guid generation, AgentRunCheckpoint checkpoint, AgentRunWait wait, DateTimeOffset now)
    {
        RequireOperational(revision, generation);
        wait = wait with { SuspendedGeneration = generation };
        wait.Validate();
        var seconds = (wait.DeadlineUtc - wait.StartedAtUtc).TotalSeconds;
        if (wait.StartedAtUtc != now || WaitCount >= AgentRunLimits.MaxWaitCount || TotalWaitSeconds + seconds > AgentRunLimits.MaxTotalWaitSeconds
            || SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Wait budget or effect fence prevents suspension.");
        return Copy(AgentRunStatus.WaitingForSignal, Revision + 1, AttemptCount, null, null,
            false, null, KnownEffectSummary, Progress, checkpoint, null, null, SideEffect, now,
            wait, WaitCount + 1, TotalWaitSeconds + seconds);
    }

    public AgentRun ResumeFromSignal(long revision, Guid generation, AgentRunCheckpoint checkpoint, DateTimeOffset now, DateTimeOffset leaseExpiresAt)
    {
        if (Revision != revision) throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "AgentRun revision is stale.");
        if (Status != AgentRunStatus.WaitingForSignal || CancellationRequested)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Execution is no longer waiting.");
        return Copy(AgentRunStatus.Running, Revision + 1, AttemptCount, null,
            new AgentRunClaim(generation, now, leaseExpiresAt), false, null, KnownEffectSummary, Progress,
            Owner.TenantId is not null ? checkpoint.WithSignalAdmissionPending() : checkpoint, null, null, SideEffect, now);
    }

    public AgentRun TakeClaim(Guid generation, DateTimeOffset claimedAtUtc, DateTimeOffset leaseExpiresAtUtc)
    {
        var resumeSameAttempt = IsApprovalResume || SignalAwaitingAdmission;
        if (CancellationRequested
            || (!resumeSameAttempt && AttemptCount >= MaxAttempts)
            || Status is not (AgentRunStatus.Queued or AgentRunStatus.WaitingToRetry)
            || (Status == AgentRunStatus.WaitingToRetry && NextRetryAtUtc > claimedAtUtc))
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Agent run cannot be claimed.");
        }

        var claim = new AgentRunClaim(generation, claimedAtUtc, leaseExpiresAtUtc);
        return Copy(
            AgentRunStatus.Running,
            Revision + 1,
            resumeSameAttempt ? AttemptCount : AttemptCount + 1,
            nextRetryAtUtc: null,
            claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            KnownEffectSummary,
            Progress,
            Checkpoint?.AwaitingRuntimeAdmission == true && !SignalAwaitingAdmission
                && !ExternalResultAwaitingAdmission(Checkpoint, SideEffect) ? Checkpoint.WithRuntimeAdmissionPending(false) : Checkpoint,
            Result,
            Failure,
            SideEffect,
            claimedAtUtc);
    }

    public AgentRun RenewClaim(long expectedRevision, Guid generation, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset renewedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        if (leaseExpiresAtUtc <= renewedAtUtc)
        {
            throw new ArgumentException("Claim lease must be later than the renewal time.");
        }

        var claim = new AgentRunClaim(generation, Claim!.ClaimedAtUtc, leaseExpiresAtUtc);
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
            renewedAtUtc);
    }

    public AgentRun SaveCheckpoint(
        long expectedRevision,
        Guid generation,
        AgentRunCheckpoint checkpoint,
        string? progressSummary,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        RequireOperational(expectedRevision, generation);
        if (Checkpoint is { } previous && (checkpoint.StepCount < previous.StepCount
            || checkpoint.OutputBytes < previous.OutputBytes
            || checkpoint.RemainingOverallBudgetMs > previous.RemainingOverallBudgetMs
            || previous.ActiveExecutionMs is { } active && (checkpoint.ActiveExecutionMs is null || checkpoint.ActiveExecutionMs < active)))
            throw new ArgumentException("Execution checkpoint accounting cannot reset consumed resources.");
        if (Admission.ExecutionBudget is { } pin && (checkpoint.StepCount > pin.Profile.MaxSteps
            || checkpoint.RemainingOverallBudgetMs > pin.Profile.DurationSeconds * 1000))
            throw new ArgumentException("Execution checkpoint exceeds its admitted budget.");
        if (checkpoint.ActiveExecutionMs is not null) checkpoint = checkpoint.StampBudgetClock(updatedAtUtc);
        var progress = progressSummary is null ? Progress : new AgentRunProgress(progressSummary, updatedAtUtc);
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
            updatedAtUtc);
    }

    public AgentRun Complete(
        long expectedRevision,
        Guid generation,
        string resultText,
        DateTimeOffset completedAtUtc,
        bool attentionRequired = false,
        AgentRunOutcomeKind outcomeKind = AgentRunOutcomeKind.Response,
        Guid? outcomeEntryId = null)
    {
        if (Status == AgentRunStatus.Completed
            && resultText is not null
            && string.Equals(Result!.Text, resultText.Trim(), StringComparison.Ordinal)
            && Result.OutcomeKind == (attentionRequired ? AgentRunOutcomeKind.NeedsAttention : outcomeKind)
            && Result.OutcomeEntryId == outcomeEntryId)
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        if (SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
        {
            throw new AgentRunTransitionException(
                AgentRunTransitionFailure.Rejected,
                "An uncertain external effect cannot be completed.");
        }

        if (resultText is null)
        {
            throw new ArgumentException("Result is required.");
        }

        return Copy(
            AgentRunStatus.Completed,
            Revision + 1,
            AttemptCount,
            nextRetryAtUtc: null,
            claim: null,
            cancellationRequested: false,
            cancellationRequestedAtUtc: null,
            knownEffectSummary: null,
            Progress,
            Checkpoint,
            new AgentRunResult(resultText, completedAtUtc, attentionRequired, outcomeKind, outcomeEntryId),
            failure: null,
            SideEffect,
            completedAtUtc);
    }

    public AgentRun Fail(
        long expectedRevision,
        Guid generation,
        string failureCode,
        string failureSummary,
        bool replaySafe,
        DateTimeOffset failedAtUtc,
        DateTimeOffset? nextRetryAtUtc,
        Func<Guid>? allocateDiagnosticId = null)
    {
        if (Status == AgentRunStatus.Failed
            && failureCode is not null
            && failureSummary is not null
            && Failure is not null
            && ((Failure.Code == failureCode.Trim() && Failure.Summary == failureSummary.Trim())
                || (Failure.Code == "attempts-exhausted"
                    && Failure.Summary == AgentRunKnownEffects.Exhausted(failureSummary))))
        {
            return this;
        }

        RequireOperational(expectedRevision, generation);
        var unsafeEffect = SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate;
        var observationRequired = IsObservationRequiredCheckpoint(Checkpoint);
        if (!replaySafe || unsafeEffect)
        {
            var effect = unsafeEffect ? AsIndeterminate(failedAtUtc) : SideEffect;
            var code = unsafeEffect
                ? observationRequired ? "observation-required" : "side-effect-indeterminate"
                : failureCode ?? throw new ArgumentException("Failure code is required.");
            var summary = unsafeEffect
                ? observationRequired
                    ? "A browser change must be observed before the agent run can run again."
                    : "External effect outcome is unknown and was not replayed."
                : failureSummary ?? throw new ArgumentException("Failure is required.");
            return AsFailed(failedAtUtc, code, summary, effect, RequireDiagnosticId(allocateDiagnosticId));
        }

        if (AttemptCount >= MaxAttempts)
        {
            return AsFailed(
                failedAtUtc,
                "attempts-exhausted",
                AgentRunKnownEffects.Exhausted(failureSummary ?? throw new ArgumentException("Failure is required.")),
                SideEffect,
                RequireDiagnosticId(allocateDiagnosticId));
        }

        if (nextRetryAtUtc is null || nextRetryAtUtc < failedAtUtc)
        {
            throw new ArgumentException("Retry time is required and cannot precede the failure.");
        }

        return Copy(
            AgentRunStatus.WaitingToRetry,
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
            new AgentRunFailure(failureCode!, failureSummary!, failedAtUtc, DiagnosticIdOrNull(allocateDiagnosticId)),
            SideEffect,
            failedAtUtc);
    }

    public AgentRun RequestCancellation(long expectedRevision, string? knownEffectSummary, DateTimeOffset requestedAtUtc)
    {
        if (Status == AgentRunStatus.Cancelled)
        {
            return this;
        }

        if (IsTerminal)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Terminal, "Terminal work cannot be cancelled.");
        }

        RequireRevision(expectedRevision);
        if (Status == AgentRunStatus.Running)
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

                requestedAtUtc);
        }

        return AsCancelled(requestedAtUtc, knownEffectSummary ?? KnownEffectSummary);
    }

    public AgentRun CommitCancellation(long expectedRevision, Guid generation, string? knownEffectSummary, DateTimeOffset cancelledAtUtc)
    {
        if (Status == AgentRunStatus.Cancelled)
        {
            return this;
        }

        RequireRevision(expectedRevision);
        if (IsTerminal)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Terminal, "Terminal work cannot be cancelled.");
        }

        if (Status != AgentRunStatus.Running || Claim is null || Claim.Generation != generation)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleGeneration, "Agent run execution generation is stale.");
        }

        if (!CancellationRequested)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Cancellation was not requested.");
        }

        return AsCancelled(cancelledAtUtc, knownEffectSummary ?? KnownEffectSummary);
    }

    public AgentRun DeferUnstartedDispatch(long expectedRevision, Guid generation, DateTimeOffset atUtc, DateTimeOffset retryAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        AgentRunTime.RequireUtc(atUtc, "Dispatch deferral");
        AgentRunTime.RequireUtc(retryAtUtc, "Dispatch retry");
        if (Checkpoint is not null || SideEffect.Disposition != AgentRunSideEffectDisposition.None || retryAtUtc <= atUtc)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Only an unstarted refused dispatch can be deferred.");
        return Copy(AgentRunStatus.WaitingToRetry, Revision + 1, AttemptCount - 1, retryAtUtc, null,
            CancellationRequested, CancellationRequestedAtUtc, KnownEffectSummary, Progress, null, Result, Failure, SideEffect, atUtc);
    }

    private bool SignalAwaitingAdmission => Owner.TenantId is not null && WaitCount > 0 && AttemptCount > 0
        && Checkpoint?.AwaitingSignalAdmission == true;

    public AgentRun DeferUnacceptedSignalDispatch(long revision, Guid generation, DateTimeOffset at, DateTimeOffset retryAt)
    {
        RequireOperational(revision, generation);
        AgentRunTime.RequireUtc(retryAt, "Signal dispatch retry");
        if (!SignalAwaitingAdmission || retryAt <= at || SideEffect.Disposition is AgentRunSideEffectDisposition.Prepared
            or AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Only an unaccepted signal continuation can defer with its checkpoint.");
        return Copy(AgentRunStatus.WaitingToRetry, checked(Revision + 1), AttemptCount, retryAt, null, false, null,
            KnownEffectSummary, Progress, Checkpoint!.PauseBudgetClock(Claim!.ClaimedAtUtc), Result, Failure, SideEffect, at);
    }

    public AgentRun RecoverExpiredClaim(DateTimeOffset asOfUtc, Func<Guid>? allocateDiagnosticId = null)
    {
        AgentRunTime.RequireUtc(asOfUtc, "Recovery");
        if (Status != AgentRunStatus.Running || Claim is null || Claim.LeaseExpiresAtUtc > asOfUtc)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.NotClaimable, "Agent run claim is still current.");
        }

        if (CancellationRequested)
        {
            return AsCancelled(asOfUtc, KnownEffectSummary);
        }

        if (SignalAwaitingAdmission || ExternalResultAwaitingAdmission(Checkpoint, SideEffect))
            return Copy(AgentRunStatus.WaitingToRetry, checked(Revision + 1), AttemptCount, asOfUtc, null, false, null, KnownEffectSummary,
                Progress, Checkpoint, Result, Failure, SideEffect, asOfUtc);

        if (SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
        {
            var flagged = IsObservationRequiredCheckpoint(Checkpoint);
            var uncertainBrowserInteraction = flagged
                || AgentRunActionHash.MatchesBrowserInteraction(Checkpoint?.PayloadJson, SideEffect.ActionHash);
            if (uncertainBrowserInteraction && Checkpoint is not null)
            {
                var generation = allocateDiagnosticId?.Invoke() ?? Guid.Empty;
                if (generation == Guid.Empty)
                {
                    throw new ArgumentException("A resumed browser observation requires a new execution claim.");
                }

                var payload = flagged ? Checkpoint.PayloadJson
                    : AgentRunActionHash.MarkObservationRequired(Checkpoint.PayloadJson, SideEffect.ActionHash!);
                if (System.Text.Encoding.UTF8.GetByteCount(payload) > AgentRunLimits.MaxCheckpointBytes)
                    return AsFailed(asOfUtc, "checkpoint-capacity",
                        "Durable checkpoint capacity is exhausted; uncertain external effect was not replayed.",
                        AsIndeterminate(asOfUtc), RequireDiagnosticId(allocateDiagnosticId));
                var checkpoint = flagged ? Checkpoint : new AgentRunCheckpoint(
                        payload,
                        Checkpoint.StepCount,
                        Checkpoint.OutputBytes,
                        Checkpoint.RemainingOverallBudgetMs, Checkpoint.ActiveExecutionMs, Checkpoint.BudgetRecordedAtUtc);
                return Copy(
                    AgentRunStatus.Running,
                    Revision + 1,
                    AttemptCount,
                    nextRetryAtUtc: null,
                    new AgentRunClaim(generation, asOfUtc, asOfUtc.AddMinutes(1)),
                    CancellationRequested,
                    CancellationRequestedAtUtc,
                    KnownEffectSummary,
                    Progress,
                    checkpoint,
                    Result,
                    Failure,
                    SideEffect,

                    asOfUtc);
            }

            return AsFailed(
                asOfUtc,
                "side-effect-indeterminate",
                "External effect outcome is unknown and was not replayed.",
                AsIndeterminate(asOfUtc),
                RequireDiagnosticId(allocateDiagnosticId));
        }

        if (AttemptCount >= MaxAttempts)
        {
            var summary = Failure is null
                ? "Retry budget is exhausted."
                : AgentRunKnownEffects.Exhausted(Failure.Summary);
            return AsFailed(
                asOfUtc,
                "attempts-exhausted",
                summary,
                SideEffect,
                RequireDiagnosticId(allocateDiagnosticId));
        }

        return Copy(
            AgentRunStatus.WaitingToRetry,
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
            asOfUtc);
    }

    public AgentRun MarkSideEffect(
        long expectedRevision,
        Guid generation,
        AgentRunSideEffectDisposition disposition,
        string toolCallId,
        string actionHash,
        DateTimeOffset updatedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        if (SideEffect.Disposition == disposition
            && string.Equals(SideEffect.ToolCallId, toolCallId, StringComparison.Ordinal)
            && string.Equals(SideEffect.ActionHash, actionHash, StringComparison.Ordinal))
        {
            return this;
        }

        if (SideEffect.ToolCallId is not null
            && !string.Equals(SideEffect.ToolCallId, toolCallId, StringComparison.Ordinal))
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Side-effect operation does not match.");
        }

        if (SideEffect.ActionHash is not null
            && !string.Equals(SideEffect.ActionHash, actionHash, StringComparison.Ordinal))
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Side-effect action does not match.");
        }

        if (!CanTransitionSideEffect(SideEffect.Disposition, disposition))
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Illegal, "Side-effect transition is not allowed.");
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
            new AgentRunSideEffect(disposition, toolCallId, actionHash, updatedAtUtc, SideEffect.ExternalEffect),
            updatedAtUtc);
    }

    public AgentRun AcceptBrowserSnapshot(long expectedRevision, Guid generation, DateTimeOffset updatedAtUtc)
    {
        RequireOperational(expectedRevision, generation);
        if (SideEffect.ExternalEffect is not null && SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate)
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "A snapshot cannot release a durable external operation fence.");
        if (SideEffect.Disposition is not (AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))
        {
            return this;
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
            AgentRunSideEffect.None,
            updatedAtUtc);
    }

    public AgentRun ClearSideEffect(
        long expectedRevision,
        Guid generation,
        DateTimeOffset updatedAtUtc,
        bool recordExternalEffect = true)
    {
        RequireOperational(expectedRevision, generation);
        if (SideEffect.Disposition == AgentRunSideEffectDisposition.None)
        {
            return this;
        }

        if (SideEffect.Disposition is not (AgentRunSideEffectDisposition.Succeeded or AgentRunSideEffectDisposition.DefinitelyFailed))
        {
            throw new AgentRunTransitionException(
                AgentRunTransitionFailure.Illegal,
                "Only a completed operation side effect can be cleared.");
        }

        var knownEffect = recordExternalEffect && SideEffect.Disposition == AgentRunSideEffectDisposition.Succeeded
            ? AgentRunKnownEffects.PreserveCompletedExternalEffect(KnownEffectSummary)
            : KnownEffectSummary;
        return Copy(
            Status,
            Revision + 1,
            AttemptCount,
            NextRetryAtUtc,
            Claim,
            CancellationRequested,
            CancellationRequestedAtUtc,
            knownEffect,
            Progress,
            Checkpoint,
            Result,
            Failure,
            AgentRunSideEffect.None,
            updatedAtUtc);
    }

    private AgentRun AsCancelled(DateTimeOffset cancelledAtUtc, string? knownEffectSummary)
    {
        var sideEffect = SideEffect;
        return Copy(
            AgentRunStatus.Cancelled,
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
            cancelledAtUtc);
    }

    private AgentRun AsFailed(
        DateTimeOffset failedAtUtc,
        string code,
        string summary,
        AgentRunSideEffect sideEffect,
        Guid diagnosticId) =>
        Copy(
            AgentRunStatus.Failed,
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
            new AgentRunFailure(code, summary, failedAtUtc, diagnosticId),
            sideEffect,
            failedAtUtc);

    private static Guid? DiagnosticIdOrNull(Func<Guid>? allocateDiagnosticId)
    {
        var diagnosticId = allocateDiagnosticId?.Invoke() ?? Guid.Empty;
        return diagnosticId == Guid.Empty ? null : diagnosticId;
    }

    private static Guid RequireDiagnosticId(Func<Guid>? allocateDiagnosticId)
    {
        var diagnosticId = allocateDiagnosticId?.Invoke() ?? Guid.Empty;
        if (diagnosticId == Guid.Empty)
        {
            throw new ArgumentException("A terminal work failure requires a diagnostic id.");
        }

        return diagnosticId;
    }

    private AgentRunSideEffect AsIndeterminate(DateTimeOffset updatedAtUtc)
    {
        if (SideEffect.Disposition == AgentRunSideEffectDisposition.Indeterminate)
        {
            return SideEffect;
        }

        return new AgentRunSideEffect(
            AgentRunSideEffectDisposition.Indeterminate,
            SideEffect.ToolCallId,
            SideEffect.ActionHash,
            updatedAtUtc,
            SideEffect.ExternalEffect);
    }

    private void RequireOperational(long expectedRevision, Guid generation)
    {
        RequireRevision(expectedRevision);
        if (IsTerminal)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Terminal, "Terminal work cannot change.");
        }

        if (Status != AgentRunStatus.Running || Claim is null || Claim.Generation != generation)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleGeneration, "Agent run execution generation is stale.");
        }

        if (CancellationRequested)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.Rejected, "Cancellation was already requested.");
        }
    }

    private void RequireRevision(long expectedRevision)
    {
        if (Revision != expectedRevision)
        {
            throw new AgentRunTransitionException(AgentRunTransitionFailure.StaleRevision, "Agent run revision is stale.");
        }
    }

    private static bool IsObservationRequiredCheckpoint(AgentRunCheckpoint? checkpoint) =>
        checkpoint?.PayloadJson.Contains("\"ObservationRequired\":true", StringComparison.Ordinal) == true;

    private bool IsApprovalResume =>
        (Status == AgentRunStatus.Queued || Status == AgentRunStatus.WaitingToRetry && Admission.ApprovalResume is not null && Checkpoint?.AwaitingRuntimeAdmission == true)
        && Owner.TenantId is not null && ApprovalRequestId is not null && AttemptCount > 0;

    private static bool CanTransitionSideEffect(AgentRunSideEffectDisposition from, AgentRunSideEffectDisposition to) =>
        (from, to) switch
        {
            (AgentRunSideEffectDisposition.None, AgentRunSideEffectDisposition.Prepared) => true,
            (AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.InFlight) => true,
            (AgentRunSideEffectDisposition.Prepared, AgentRunSideEffectDisposition.DefinitelyFailed) => true,
            (AgentRunSideEffectDisposition.InFlight, AgentRunSideEffectDisposition.Succeeded) => true,
            (AgentRunSideEffectDisposition.InFlight, AgentRunSideEffectDisposition.DefinitelyFailed) => true,
            (AgentRunSideEffectDisposition.InFlight, AgentRunSideEffectDisposition.Indeterminate) => true,
            _ => false
        };

    private AgentRun Copy(
        AgentRunStatus status,
        long revision,
        int attemptCount,
        DateTimeOffset? nextRetryAtUtc,
        AgentRunClaim? claim,
        bool cancellationRequested,
        DateTimeOffset? cancellationRequestedAtUtc,
        string? knownEffectSummary,
        AgentRunProgress? progress,
        AgentRunCheckpoint? checkpoint,
        AgentRunResult? result,
        AgentRunFailure? failure,
        AgentRunSideEffect sideEffect,
        DateTimeOffset updatedAtUtc, AgentRunWait? wait = null, int? waitCount = null, double? totalWaitSeconds = null, Guid? approvalRequestId = null)
    {
        if (updatedAtUtc < UpdatedAtUtc)
        {
            throw new ArgumentException("Updated time cannot move backwards.");
        }

        if (ExternalResultAwaitingAdmission(checkpoint, sideEffect) || Owner.TenantId is not null && checkpoint?.AwaitingSignalAdmission == true)
            checkpoint = checkpoint!.PauseBudgetClock(updatedAtUtc);
        else if (Status == AgentRunStatus.Running && status == AgentRunStatus.Running && Claim?.Generation != claim?.Generation && checkpoint is not null)
            checkpoint = checkpoint.PauseBudgetClock(Claim is { } expired && expired.LeaseExpiresAtUtc < updatedAtUtc
                ? expired.LeaseExpiresAtUtc : updatedAtUtc).StampBudgetClock(updatedAtUtc);
        else if (Status == AgentRunStatus.Running && status != AgentRunStatus.Running && checkpoint is not null)
            checkpoint = checkpoint.PauseBudgetClock(Claim is { } oldClaim && oldClaim.LeaseExpiresAtUtc < updatedAtUtc
                ? oldClaim.LeaseExpiresAtUtc : updatedAtUtc);
        else if (Status != AgentRunStatus.Running && status == AgentRunStatus.Running && checkpoint?.ActiveExecutionMs is not null)
            checkpoint = checkpoint.StampBudgetClock(updatedAtUtc);
        return new(
            AgentRunId,
            Owner,
            Admission,
            PinnedModel,
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
            CreatedAtUtc,
            updatedAtUtc, PinnedSkillCatalog, ActiveSkillKeys, SkillLoadCount, LoadedCapabilityIds, CapabilityLoadCount,
            status == AgentRunStatus.WaitingForSignal ? wait ?? Wait : null, waitCount ?? WaitCount, totalWaitSeconds ?? TotalWaitSeconds, approvalRequestId ?? ApprovalRequestId);
    }
}
