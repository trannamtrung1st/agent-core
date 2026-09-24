using AgentCore.Domain.Work;

namespace AgentCore.Domain.Tests;

public sealed class WorkItemContractTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-0008-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-0008-7000-8000-0000000000b1");
    private static readonly Guid WorkItemId = Guid.Parse("019944af-0008-7000-8000-0000000000c1");
    private static readonly Guid SourceId = Guid.Parse("019944af-0008-7000-8000-0000000000d1");
    private static readonly Guid GenerationA = Guid.Parse("019944af-0008-7000-8000-0000000000e1");
    private static readonly Guid GenerationB = Guid.Parse("019944af-0008-7000-8000-0000000000e2");
    private static readonly Guid ApprovalId = Guid.Parse("019944af-0008-7000-8000-0000000000f1");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
    private const string ActionHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public void Owner_requires_instance_and_profile()
    {
        Assert.Throws<ArgumentException>(() => new WorkOwner(Guid.Empty, ProfileId));
        Assert.Throws<ArgumentException>(() => new WorkOwner(InstanceId, Guid.Empty));
        var owner = new WorkOwner(InstanceId, ProfileId);
        Assert.Equal(InstanceId, owner.AgentInstanceId);
        Assert.Equal(ProfileId, owner.ProfileId);
    }

    [Fact]
    public void Initial_item_is_queued_without_a_claim()
    {
        var item = NewItem();
        Assert.True(item.IsInitialQueued);
        Assert.False(item.HasLiveClaim);
        Assert.False(item.IsTerminal);
        Assert.Equal(WorkItemStatus.Queued, item.Status);
        Assert.Equal(1, item.Revision);
        Assert.Equal(WorkSourceKind.Schedule, item.Provenance.SourceKind);
        Assert.Equal("Scheduled reminder", item.OriginLabel);
        Assert.Equal("synthetic", item.Model.ProviderAlias);
        Assert.Throws<ArgumentException>(() => NewItem(maxAttempts: 0));
        Assert.Throws<ArgumentException>(() => NewItem(evidence: new string('a', WorkLimits.MaxEvidenceBytes + 1)));
    }

    [Fact]
    public void Source_kind_preserves_application_events()
    {
        var item = NewItem(kind: WorkSourceKind.ApplicationEvent);
        Assert.Equal(WorkSourceKind.ApplicationEvent, item.Provenance.SourceKind);
        Assert.Equal("Application event", item.OriginLabel);
    }

    [Fact]
    public void Claim_checkpoint_and_completion_require_the_current_generation()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        Assert.Equal(WorkItemStatus.Running, claimed.Status);
        Assert.Equal(GenerationA, claimed.Claim!.Generation);
        Assert.Equal(2, claimed.Revision);
        Assert.Equal(1, claimed.AttemptCount);

        var checkpoint = claimed.SaveCheckpoint(2, GenerationA, Checkpoint("SECRET_CHECKPOINT"), "Delivering reminder", Now.AddSeconds(1));
        Assert.Equal(3, checkpoint.Revision);
        Assert.Equal("SECRET_CHECKPOINT", checkpoint.Checkpoint!.PayloadJson);
        Assert.Equal("Delivering reminder", checkpoint.Progress!.Summary);

        var stale = Assert.Throws<WorkItemTransitionException>(() =>
            checkpoint.SaveCheckpoint(2, GenerationA, Checkpoint("other"), null, Now.AddSeconds(2)));
        Assert.Equal(WorkTransitionFailure.StaleRevision, stale.Failure);
        var wrongGeneration = Assert.Throws<WorkItemTransitionException>(() =>
            checkpoint.SaveCheckpoint(3, GenerationB, Checkpoint("other"), null, Now.AddSeconds(2)));
        Assert.Equal(WorkTransitionFailure.StaleGeneration, wrongGeneration.Failure);

        var completed = checkpoint.Complete(3, GenerationA, "Reminder delivered.", Now.AddSeconds(2));
        Assert.Equal(WorkItemStatus.Completed, completed.Status);
        Assert.False(completed.HasLiveClaim);
        Assert.Equal("Reminder delivered.", completed.Result!.Text);
        var repeated = completed.Complete(4, GenerationA, "Reminder delivered.", Now.AddSeconds(3));
        Assert.Equal(4, repeated.Revision);
        Assert.Throws<WorkItemTransitionException>(() => completed.TakeClaim(GenerationB, Now.AddSeconds(3), Now.AddMinutes(2)));
    }

    [Fact]
    public void Terminal_work_cannot_be_rewritten()
    {
        var completed = CompletedItem();
        var claim = Assert.Throws<WorkItemTransitionException>(() => completed.TakeClaim(GenerationB, Now.AddMinutes(2), Now.AddMinutes(3)));
        Assert.Equal(WorkTransitionFailure.NotClaimable, claim.Failure);
        var cancel = Assert.Throws<WorkItemTransitionException>(() => completed.RequestCancellation(completed.Revision, null, Now.AddMinutes(2)));
        Assert.Equal(WorkTransitionFailure.Terminal, cancel.Failure);
        var overwrite = Assert.Throws<WorkItemTransitionException>(() =>
            completed.Complete(completed.Revision, GenerationA, "A different result.", Now.AddMinutes(2)));
        Assert.Equal(WorkTransitionFailure.Terminal, overwrite.Failure);
    }

    [Fact]
    public void Cancellation_blocks_later_completion_and_clears_the_claim()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var requested = claimed.RequestCancellation(2, "External write may have started.", Now.AddSeconds(1));
        Assert.Equal(WorkItemStatus.Running, requested.Status);
        Assert.True(requested.CancellationRequested);
        Assert.Equal(GenerationA, requested.Claim!.Generation);
        var blocked = Assert.Throws<WorkItemTransitionException>(() =>
            requested.Complete(3, GenerationA, "Should not complete.", Now.AddSeconds(2)));
        Assert.Equal(WorkTransitionFailure.Rejected, blocked.Failure);

        var cancelled = requested.CommitCancellation(3, GenerationA, null, Now.AddSeconds(2));
        Assert.Equal(WorkItemStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.HasLiveClaim);
        Assert.Equal("External write may have started.", cancelled.KnownEffectSummary);
        Assert.Equal(cancelled.Revision, cancelled.RequestCancellation(1, null, Now.AddSeconds(3)).Revision);

        var queued = NewItem(id: Guid.Parse("019944af-0008-7000-8000-0000000000c2"));
        var immediately = queued.RequestCancellation(1, null, Now.AddSeconds(1));
        Assert.Equal(WorkItemStatus.Cancelled, immediately.Status);
        Assert.False(immediately.HasLiveClaim);
    }

    [Fact]
    public void Expired_claim_recovery_is_bounded_and_fences_the_old_generation()
    {
        var claimed = NewItem(maxAttempts: 2).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var recovered = claimed.RecoverExpiredClaim(Now.AddMinutes(1));
        Assert.Equal(WorkItemStatus.WaitingToRetry, recovered.Status);
        Assert.False(recovered.HasLiveClaim);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Throws<WorkItemTransitionException>(() =>
            recovered.SaveCheckpoint(recovered.Revision, GenerationA, Checkpoint("late"), null, Now.AddMinutes(2)));

        var second = recovered.TakeClaim(GenerationB, Now.AddMinutes(1), Now.AddMinutes(2));
        Assert.Equal(2, second.AttemptCount);
        var exhausted = second.RecoverExpiredClaim(Now.AddMinutes(2));
        Assert.Equal(WorkItemStatus.Failed, exhausted.Status);
        Assert.Equal("attempts-exhausted", exhausted.Failure!.Code);
        Assert.True(exhausted.IsTerminal);
        Assert.Throws<WorkItemTransitionException>(() => exhausted.TakeClaim(GenerationA, Now.AddMinutes(3), Now.AddMinutes(4)));
    }

    [Fact]
    public void Cancellation_during_a_lost_claim_wins_over_retry()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var requested = claimed.RequestCancellation(2, null, Now.AddSeconds(30));
        var recovered = requested.RecoverExpiredClaim(Now.AddMinutes(1));
        Assert.Equal(WorkItemStatus.Cancelled, recovered.Status);
        Assert.False(recovered.HasLiveClaim);
    }

    [Fact]
    public void Indeterminate_side_effect_does_not_return_to_a_runnable_state()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var prepared = claimed.MarkSideEffect(2, GenerationA, WorkSideEffectDisposition.Prepared, ActionHash, Now.AddSeconds(1));
        var inFlight = prepared.MarkSideEffect(3, GenerationA, WorkSideEffectDisposition.InFlight, ActionHash, Now.AddSeconds(2));
        var recovered = inFlight.RecoverExpiredClaim(Now.AddMinutes(1));
        Assert.Equal(WorkItemStatus.Failed, recovered.Status);
        Assert.Equal(WorkSideEffectDisposition.Indeterminate, recovered.SideEffect.Disposition);
        Assert.Equal("side-effect-indeterminate", recovered.Failure!.Code);
        Assert.Throws<WorkItemTransitionException>(() => recovered.TakeClaim(GenerationB, Now.AddMinutes(2), Now.AddMinutes(3)));
        var replay = Assert.Throws<WorkItemTransitionException>(() =>
            recovered.MarkSideEffect(recovered.Revision, GenerationA, WorkSideEffectDisposition.Succeeded, ActionHash, Now.AddMinutes(2)));
        Assert.Equal(WorkTransitionFailure.Terminal, replay.Failure);
    }

    [Fact]
    public void Approval_wait_releases_the_claim_and_binds_the_exact_action()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var waiting = claimed.BeginApproval(
            2,
            GenerationA,
            ApprovalId,
            "demo.sensitive_action",
            """{"to":"SECRET_ACTION"}""",
            ActionHash,
            "Send the weekly note",
            Now.AddMinutes(10),
            Now.AddSeconds(1));
        Assert.Equal(WorkItemStatus.WaitingForApproval, waiting.Status);
        Assert.False(waiting.HasLiveClaim);
        Assert.Equal(2, waiting.Approval!.CheckpointRevision);
        Assert.Equal(GenerationA, waiting.Approval.ExecutionGeneration);
        Assert.False(waiting.Approval.Consumed);

        var mismatch = Assert.Throws<WorkItemTransitionException>(() =>
            waiting.DecideApproval(ApprovalId, 3, 1, OtherHash, WorkApprovalDecision.Approved, Now.AddSeconds(2)));
        Assert.Equal(WorkTransitionFailure.ApprovalMismatch, mismatch.Failure);

        var approved = waiting.DecideApproval(ApprovalId, 3, 1, ActionHash, WorkApprovalDecision.Approved, Now.AddSeconds(2));
        Assert.Equal(WorkItemStatus.Queued, approved.Status);
        Assert.True(approved.Approval!.Consumed);
        Assert.Equal(approved.WorkItemId, waiting.WorkItemId);
        Assert.Equal(approved.Revision, approved.DecideApproval(ApprovalId, 1, 1, ActionHash, WorkApprovalDecision.Approved, Now.AddSeconds(3)).Revision);
        var altered = Assert.Throws<WorkItemTransitionException>(() =>
            approved.DecideApproval(ApprovalId, approved.Revision, 2, OtherHash, WorkApprovalDecision.Approved, Now.AddSeconds(3)));
        Assert.Equal(WorkTransitionFailure.ApprovalMismatch, altered.Failure);

        var rejectedWait = NewItem(id: Guid.Parse("019944af-0008-7000-8000-0000000000c3"))
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .BeginApproval(2, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(1));
        var expired = Assert.Throws<WorkItemTransitionException>(() =>
            rejectedWait.DecideApproval(ApprovalId, 3, 1, ActionHash, WorkApprovalDecision.Approved, Now.AddMinutes(10)));
        Assert.Equal(WorkTransitionFailure.ApprovalMismatch, expired.Failure);
        var closed = rejectedWait.ExpireApproval(3, Now.AddMinutes(10));
        Assert.Equal(WorkItemStatus.Queued, closed.Status);
        Assert.Equal(WorkApprovalDecision.Expired, closed.Approval!.Decision);
        Assert.False(closed.Approval.Consumed);
        Assert.False(closed.HasLiveClaim);
    }

    [Fact]
    public void Safe_retry_stops_at_the_attempt_budget()
    {
        var claimed = NewItem(maxAttempts: 2).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var retrying = claimed.Fail(2, GenerationA, "model-unavailable", "Model timed out.", true, Now.AddSeconds(1), Now.AddSeconds(2));
        Assert.Equal(WorkItemStatus.WaitingToRetry, retrying.Status);
        Assert.False(retrying.HasLiveClaim);
        Assert.Null(retrying.Failure);
        var again = retrying.TakeClaim(GenerationB, Now.AddSeconds(2), Now.AddMinutes(2));
        var failed = again.Fail(again.Revision, GenerationB, "model-unavailable", "Model timed out.", true, Now.AddSeconds(3), Now.AddSeconds(4));
        Assert.Equal(WorkItemStatus.Failed, failed.Status);
        Assert.Equal("model-unavailable", failed.Failure!.Code);
        Assert.Equal(failed.Revision, failed.Fail(1, GenerationB, "model-unavailable", "Model timed out.", true, Now.AddSeconds(5), null).Revision);
    }

    [Fact]
    public void Public_summary_omits_evidence_checkpoint_and_prepared_action()
    {
        var waiting = NewItem(evidence: """{"instruction":"SECRET_EVIDENCE"}""")
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .SaveCheckpoint(2, GenerationA, Checkpoint("SECRET_CHECKPOINT"), "Delivering reminder", Now.AddSeconds(1))
            .BeginApproval(
                3,
                GenerationA,
                ApprovalId,
                "demo.sensitive_action",
                """{"body":"SECRET_ACTION"}""",
                ActionHash,
                "Send the weekly note",
                Now.AddMinutes(10),
                Now.AddSeconds(2));
        var summary = waiting.ToPublicSummary();
        var rendered = string.Join(
            '\n',
            summary.OriginLabel,
            summary.ProgressSummary,
            summary.ApprovalPreview,
            summary.ResultText,
            summary.FailureCode,
            summary.FailureSummary,
            summary.KnownEffectSummary,
            summary.Status.ToString());
        Assert.DoesNotContain("SECRET_EVIDENCE", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_CHECKPOINT", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_ACTION", rendered, StringComparison.Ordinal);
        Assert.Equal("Send the weekly note", summary.ApprovalPreview);
        Assert.True(summary.NeedsApproval);
        Assert.Equal("Delivering reminder", summary.ProgressSummary);
        Assert.Equal("Scheduled reminder", summary.OriginLabel);
        Assert.DoesNotContain("lease", rendered, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkItem CompletedItem() =>
        NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .Complete(2, GenerationA, "Reminder delivered.", Now.AddSeconds(1));

    private static WorkCheckpoint Checkpoint(string payload) => new(payload, 1, 32, 120_000);

    private static WorkItem NewItem(
        Guid? id = null,
        Guid? sourceId = null,
        int maxAttempts = 3,
        WorkSourceKind kind = WorkSourceKind.Schedule,
        string? evidence = null) =>
        WorkItem.Create(
            id ?? WorkItemId,
            new WorkOwner(InstanceId, ProfileId),
            new WorkProvenance(
                sourceId ?? SourceId,
                kind,
                Guid.Parse("019944af-0008-7000-8000-000000000091"),
                Guid.Parse("019944af-0008-7000-8000-000000000092"),
                null,
                "registration|1|1758600000000",
                Now,
                Now,
                evidence ?? """{"kind":"reminder"}""",
                "general-assistant",
                10,
                "Alex"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", null),
            maxAttempts,
            Now);
}
