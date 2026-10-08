using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class AgentRunContractTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-0008-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-0008-7000-8000-0000000000b1");
    private static readonly Guid AgentRunId = Guid.Parse("019944af-0008-7000-8000-0000000000c1");
    private static readonly Guid SourceId = Guid.Parse("019944af-0008-7000-8000-0000000000d1");
    private static readonly Guid GenerationA = Guid.Parse("019944af-0008-7000-8000-0000000000e1");
    private static readonly Guid GenerationB = Guid.Parse("019944af-0008-7000-8000-0000000000e2");
    private static readonly Guid ApprovalId = Guid.Parse("019944af-0008-7000-8000-0000000000f1");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 1, 0, 0, TimeSpan.Zero);
    private const string ActionHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ToolCallId = "tool-call-a";
    private const string OtherToolCallId = "tool-call-b";

    [Fact]
    public void Owner_requires_instance_and_profile()
    {
        Assert.Throws<ArgumentException>(() => new AgentRunOwner(Guid.Empty, ProfileId));
        Assert.Throws<ArgumentException>(() => new AgentRunOwner(InstanceId, Guid.Empty));
        var owner = new AgentRunOwner(InstanceId, ProfileId);
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
        Assert.Equal(AgentRunStatus.Queued, item.Status);
        Assert.Equal(1, item.Revision);
        Assert.Equal(ActivationKind.ScheduledWork, item.Admission.Activation.Kind);
        Assert.Equal("synthetic", item.PinnedModel.ProviderAlias);
        Assert.Throws<ArgumentException>(() => NewItem(maxAttempts: 0));
    }

    [Fact]
    public void Claim_checkpoint_and_completion_require_the_current_generation()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        Assert.Equal(AgentRunStatus.Running, claimed.Status);
        Assert.Equal(GenerationA, claimed.Claim!.Generation);
        Assert.Equal(2, claimed.Revision);
        Assert.Equal(1, claimed.AttemptCount);

        var checkpoint = claimed.SaveCheckpoint(2, GenerationA, Checkpoint("SECRET_CHECKPOINT"), "Delivering reminder", Now.AddSeconds(1));
        Assert.Equal(3, checkpoint.Revision);
        Assert.Equal("SECRET_CHECKPOINT", checkpoint.Checkpoint!.PayloadJson);
        Assert.Equal("Delivering reminder", checkpoint.Progress!.Summary);

        var stale = Assert.Throws<AgentRunTransitionException>(() =>
            checkpoint.SaveCheckpoint(2, GenerationA, Checkpoint("other"), null, Now.AddSeconds(2)));
        Assert.Equal(AgentRunTransitionFailure.StaleRevision, stale.Failure);
        var wrongGeneration = Assert.Throws<AgentRunTransitionException>(() =>
            checkpoint.SaveCheckpoint(3, GenerationB, Checkpoint("other"), null, Now.AddSeconds(2)));
        Assert.Equal(AgentRunTransitionFailure.StaleGeneration, wrongGeneration.Failure);

        var completed = checkpoint.Complete(3, GenerationA, "Reminder delivered.", Now.AddSeconds(2), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094"));
        Assert.Equal(AgentRunStatus.Completed, completed.Status);
        Assert.False(completed.HasLiveClaim);
        Assert.Equal("Reminder delivered.", completed.Result!.Text);
        var repeated = completed.Complete(4, GenerationA, "Reminder delivered.", Now.AddSeconds(3), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094"));
        Assert.Equal(4, repeated.Revision);
        Assert.Throws<AgentRunTransitionException>(() => completed.TakeClaim(GenerationB, Now.AddSeconds(3), Now.AddMinutes(2)));
    }

    [Fact]
    public void Terminal_work_cannot_be_rewritten()
    {
        var completed = CompletedItem();
        var claim = Assert.Throws<AgentRunTransitionException>(() => completed.TakeClaim(GenerationB, Now.AddMinutes(2), Now.AddMinutes(3)));
        Assert.Equal(AgentRunTransitionFailure.NotClaimable, claim.Failure);
        var cancel = Assert.Throws<AgentRunTransitionException>(() => completed.RequestCancellation(completed.Revision, null, Now.AddMinutes(2)));
        Assert.Equal(AgentRunTransitionFailure.Terminal, cancel.Failure);
        var overwrite = Assert.Throws<AgentRunTransitionException>(() =>
            completed.Complete(completed.Revision, GenerationA, "A different result.", Now.AddMinutes(2), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094")));
        Assert.Equal(AgentRunTransitionFailure.Terminal, overwrite.Failure);
    }

    [Fact]
    public void Cancellation_blocks_later_completion_and_clears_the_claim()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var requested = claimed.RequestCancellation(2, "External write may have started.", Now.AddSeconds(1));
        Assert.Equal(AgentRunStatus.Running, requested.Status);
        Assert.True(requested.CancellationRequested);
        Assert.Equal(GenerationA, requested.Claim!.Generation);
        var blocked = Assert.Throws<AgentRunTransitionException>(() =>
            requested.Complete(3, GenerationA, "Should not complete.", Now.AddSeconds(2), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094")));
        Assert.Equal(AgentRunTransitionFailure.Rejected, blocked.Failure);

        var cancelled = requested.CommitCancellation(3, GenerationA, null, Now.AddSeconds(2));
        Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.HasLiveClaim);
        Assert.Equal("External write may have started.", cancelled.KnownEffectSummary);
        Assert.Equal(cancelled.Revision, cancelled.RequestCancellation(1, null, Now.AddSeconds(3)).Revision);

        var queued = NewItem(id: Guid.Parse("019944af-0008-7000-8000-0000000000c2"));
        var immediately = queued.RequestCancellation(1, null, Now.AddSeconds(1));
        Assert.Equal(AgentRunStatus.Cancelled, immediately.Status);
        Assert.False(immediately.HasLiveClaim);
    }

    [Fact]
    public void Expired_claim_recovery_is_bounded_and_fences_the_old_generation()
    {
        var claimed = NewItem(maxAttempts: 2).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var recovered = claimed.RecoverExpiredClaim(Now.AddMinutes(1));
        Assert.Equal(AgentRunStatus.WaitingToRetry, recovered.Status);
        Assert.False(recovered.HasLiveClaim);
        Assert.Equal(1, recovered.AttemptCount);
        Assert.Throws<AgentRunTransitionException>(() =>
            recovered.SaveCheckpoint(recovered.Revision, GenerationA, Checkpoint("late"), null, Now.AddMinutes(2)));

        var second = recovered.TakeClaim(GenerationB, Now.AddMinutes(1), Now.AddMinutes(2));
        Assert.Equal(2, second.AttemptCount);
        var exhaustedId = Guid.Parse("019944af-0008-7000-8000-0000000000d1");
        var exhausted = second.RecoverExpiredClaim(Now.AddMinutes(2), () => exhaustedId);
        Assert.Equal(AgentRunStatus.Failed, exhausted.Status);
        Assert.Equal("attempts-exhausted", exhausted.Failure!.Code);
        Assert.Equal(exhaustedId, exhausted.Failure.DiagnosticId);
        Assert.Null(recovered.Failure);
        Assert.Equal("Retry budget is exhausted.", exhausted.Failure.Summary);
        Assert.True(exhausted.IsTerminal);
        Assert.Throws<AgentRunTransitionException>(() => exhausted.TakeClaim(GenerationA, Now.AddMinutes(3), Now.AddMinutes(4)));
    }

    [Fact]
    public void Retry_keeps_the_last_attempt_reason_until_the_budget_is_exhausted()
    {
        var claimed = NewItem(maxAttempts: 2).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var retryId = Guid.Parse("019944af-0008-7000-8000-0000000000e1");
        var retry = claimed.Fail(
            claimed.Revision,
            GenerationA,
            "empty-result",
            "The model returned no result.",
            true,
            Now.AddSeconds(1),
            Now.AddMinutes(1),
            () => retryId);
        Assert.Equal(AgentRunStatus.WaitingToRetry, retry.Status);
        Assert.Equal("empty-result", retry.Failure!.Code);
        Assert.Equal(retryId, retry.Failure.DiagnosticId);

        var second = retry.TakeClaim(GenerationB, Now.AddMinutes(1), Now.AddMinutes(2));
        var exhaustedId = Guid.Parse("019944af-0008-7000-8000-0000000000e2");
        var exhausted = second.Fail(
            second.Revision,
            GenerationB,
            "empty-result",
            "The model returned no result.",
            true,
            Now.AddMinutes(1).AddSeconds(1),
            null,
            () => exhaustedId);
        Assert.Equal(AgentRunStatus.Failed, exhausted.Status);
        Assert.Equal("attempts-exhausted", exhausted.Failure!.Code);
        Assert.Equal(
            "Retry budget is exhausted. Last attempt: The model returned no result.",
            exhausted.Failure.Summary);
        Assert.Equal(exhaustedId, exhausted.Failure.DiagnosticId);

        var lost = second.RecoverExpiredClaim(Now.AddMinutes(2), () => Guid.Parse("019944af-0008-7000-8000-0000000000e3"));
        Assert.Equal("attempts-exhausted", lost.Failure!.Code);
        Assert.Contains("The model returned no result.", lost.Failure.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Exhausted_summary_stays_inside_the_failure_line_limit()
    {
        var last = new string('a', AgentRunLimits.MaxFailureSummaryCharacters);
        var text = AgentRunKnownEffects.Exhausted(last);
        Assert.True(text.Length <= AgentRunLimits.MaxFailureSummaryCharacters);
        Assert.StartsWith("Retry budget is exhausted. Last attempt: ", text, StringComparison.Ordinal);
        Assert.EndsWith(".", text, StringComparison.Ordinal);
        _ = new AgentRunFailure("attempts-exhausted", text, Now, Guid.NewGuid());
    }

    [Fact]
    public void Clearing_a_read_only_fence_does_not_claim_an_external_action()
    {
        var cleared = NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1))
            .MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(2))
            .MarkSideEffect(4, GenerationA, AgentRunSideEffectDisposition.Succeeded, ToolCallId, ActionHash, Now.AddSeconds(3))
            .ClearSideEffect(5, GenerationA, Now.AddSeconds(4), recordExternalEffect: false);
        Assert.Equal(AgentRunSideEffectDisposition.None, cleared.SideEffect.Disposition);
        Assert.Null(cleared.KnownEffectSummary);
    }

    [Fact]
    public void Cancellation_during_a_lost_claim_wins_over_retry()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var requested = claimed.RequestCancellation(2, null, Now.AddSeconds(30));
        var recovered = requested.RecoverExpiredClaim(Now.AddMinutes(1));
        Assert.Equal(AgentRunStatus.Cancelled, recovered.Status);
        Assert.False(recovered.HasLiveClaim);
    }

    [Fact]
    public void Indeterminate_side_effect_does_not_return_to_a_runnable_state()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var prepared = claimed.MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1));
        var inFlight = prepared.MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(2));
        var recoveredId = Guid.Parse("019944af-0008-7000-8000-0000000000d2");
        var recovered = inFlight.RecoverExpiredClaim(Now.AddMinutes(1), () => recoveredId);
        Assert.Equal(AgentRunStatus.Failed, recovered.Status);
        Assert.Equal(recoveredId, recovered.Failure!.DiagnosticId);
        Assert.Equal(AgentRunSideEffectDisposition.Indeterminate, recovered.SideEffect.Disposition);
        Assert.Equal("side-effect-indeterminate", recovered.Failure!.Code);
        Assert.Throws<AgentRunTransitionException>(() => recovered.TakeClaim(GenerationB, Now.AddMinutes(2), Now.AddMinutes(3)));
        var replay = Assert.Throws<AgentRunTransitionException>(() =>
            recovered.MarkSideEffect(recovered.Revision, GenerationA, AgentRunSideEffectDisposition.Succeeded, ToolCallId, ActionHash, Now.AddMinutes(2)));
        Assert.Equal(AgentRunTransitionFailure.Terminal, replay.Failure);
    }

    [Fact]
    public void Observation_required_claim_expiry_resumes_without_a_runnable_retry()
    {
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var prepared = claimed.MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1));
        var inFlight = prepared.MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(2));
        var checkpoint = inFlight.SaveCheckpoint(
            inFlight.Revision,
            GenerationA,
            Checkpoint("""{"phase":"model-turn","ObservationRequired":true}"""),
            null,
            Now.AddSeconds(3));
        var resumeId = Guid.Parse("019944af-0008-7000-8000-0000000000d3");
        var recovered = checkpoint.RecoverExpiredClaim(Now.AddMinutes(1), () => resumeId);
        Assert.Equal(AgentRunStatus.Running, recovered.Status);
        Assert.NotEqual(AgentRunStatus.WaitingToRetry, recovered.Status);
        Assert.Null(recovered.Failure);
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, recovered.SideEffect.Disposition);
        Assert.Equal(resumeId, recovered.Claim!.Generation);
        Assert.Equal(checkpoint.AttemptCount, recovered.AttemptCount);
        Assert.Contains("\"ObservationRequired\":true", recovered.Checkpoint!.PayloadJson, StringComparison.Ordinal);
        Assert.Throws<AgentRunTransitionException>(() => recovered.TakeClaim(GenerationB, Now.AddMinutes(2), Now.AddMinutes(3)));
    }

    [Theory]
    [InlineData("browser.click")]
    [InlineData("browser.type")]
    [InlineData("browser.fill_form")]
    [InlineData("browser.press_key")]
    [InlineData("browser.tabs")]
    [InlineData("browser.route")]
    [InlineData("browser.cookies")]
    [InlineData("browser.emulate_media")]
    public void In_flight_browser_interaction_without_the_flag_resumes_for_snapshot(string toolName)
    {
        const string arguments = """{"ref":"el_0123456789abcdefghijkl"}""";
        var hash = AgentRunActionHash.Compute(toolName, JsonDocument.Parse(arguments).RootElement);
        var payload = JsonSerializer.Serialize(new
        {
            Phase = "model-turn",
            Messages = new[]
            {
                new
                {
                    Role = "Assistant",
                    Text = "",
                    ToolCallId = (string?)null,
                    Name = (string?)null,
                    ToolCalls = new[]
                    {
                        new { Id = ToolCallId, Name = toolName, ArgumentsJson = arguments }
                    }
                }
            },
            ObservationRequired = false,
            BlockedActionHash = (string?)null
        });
        var claimed = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var prepared = claimed.MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, hash, Now.AddSeconds(1));
        var inFlight = prepared.MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, hash, Now.AddSeconds(2));
        var checkpoint = inFlight.SaveCheckpoint(inFlight.Revision, GenerationA, Checkpoint(payload), null, Now.AddSeconds(3));
        var resumeId = Guid.Parse("019944af-0008-7000-8000-0000000000d4");
        var recovered = checkpoint.RecoverExpiredClaim(Now.AddMinutes(1), () => resumeId);
        Assert.Equal(AgentRunStatus.Running, recovered.Status);
        Assert.NotEqual(AgentRunStatus.WaitingToRetry, recovered.Status);
        Assert.Null(recovered.Failure);
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, recovered.SideEffect.Disposition);
        Assert.Contains("\"ObservationRequired\":true", recovered.Checkpoint!.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(hash, recovered.Checkpoint.PayloadJson, StringComparison.Ordinal);

        // A legacy full checkpoint has no room for the recovery flag/hash. Fail closed rather than throw or replay.
        var fullPayload = payload.Replace("\"Text\":\"\"", "\"Text\":\"" +
            new string('x', AgentRunLimits.MaxCheckpointBytes - System.Text.Encoding.UTF8.GetByteCount(payload)) + "\"", StringComparison.Ordinal);
        var fullCheckpoint = inFlight.SaveCheckpoint(inFlight.Revision, GenerationA, Checkpoint(fullPayload), null, Now.AddSeconds(3));
        var capacity = fullCheckpoint.RecoverExpiredClaim(Now.AddMinutes(1), () => resumeId);
        Assert.Equal(AgentRunStatus.Failed, capacity.Status);
        Assert.Equal("checkpoint-capacity", capacity.Failure!.Code);
        Assert.Equal(AgentRunSideEffectDisposition.Indeterminate, capacity.SideEffect.Disposition);

        foreach (var readOnlyTool in new[] { "browser.navigate", "browser.pdf" })
        {
            var navigation = AgentRunActionHash.Compute(readOnlyTool, JsonDocument.Parse(arguments).RootElement);
            var navigatePayload = payload.Replace(toolName, readOnlyTool, StringComparison.Ordinal);
            var navigateClaim = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
            var navigatePrepared = navigateClaim.MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, navigation, Now.AddSeconds(1));
            var navigateFlight = navigatePrepared.MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, navigation, Now.AddSeconds(2));
            var navigateCheckpoint = navigateFlight.SaveCheckpoint(
                navigateFlight.Revision,
                GenerationA,
                Checkpoint(navigatePayload),
                null,
                Now.AddSeconds(3));
            var failed = navigateCheckpoint.RecoverExpiredClaim(Now.AddMinutes(1), () => Guid.Parse("019944af-0008-7000-8000-0000000000d5"));
            Assert.Equal(AgentRunStatus.Failed, failed.Status);
            Assert.Equal("side-effect-indeterminate", failed.Failure!.Code);
        }
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
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.False(waiting.HasLiveClaim);
        Assert.Equal(2, waiting.Approval!.CheckpointRevision);
        Assert.Equal(GenerationA, waiting.Approval.ExecutionGeneration);
        Assert.False(waiting.Approval.Consumed);

        var mismatch = Assert.Throws<AgentRunTransitionException>(() =>
            waiting.DecideApproval(ApprovalId, 3, 1, OtherHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(2)));
        Assert.Equal(AgentRunTransitionFailure.ApprovalMismatch, mismatch.Failure);

        var approved = waiting.DecideApproval(ApprovalId, 3, 1, ActionHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(2));
        Assert.Equal(AgentRunStatus.Queued, approved.Status);
        Assert.True(approved.Approval!.Consumed);
        Assert.Equal(approved.AgentRunId, waiting.AgentRunId);
        Assert.Equal(approved.Revision, approved.DecideApproval(ApprovalId, 1, 1, ActionHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(3)).Revision);
        var altered = Assert.Throws<AgentRunTransitionException>(() =>
            approved.DecideApproval(ApprovalId, approved.Revision, 2, OtherHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(3)));
        Assert.Equal(AgentRunTransitionFailure.ApprovalMismatch, altered.Failure);

        var rejectedWait = NewItem(id: Guid.Parse("019944af-0008-7000-8000-0000000000c3"))
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .BeginApproval(2, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(1));
        var expired = Assert.Throws<AgentRunTransitionException>(() =>
            rejectedWait.DecideApproval(ApprovalId, 3, 1, ActionHash, AgentRunApprovalDecision.Approved, Now.AddMinutes(10)));
        Assert.Equal(AgentRunTransitionFailure.ApprovalMismatch, expired.Failure);
        var closed = rejectedWait.ExpireApproval(3, Now.AddMinutes(10));
        Assert.Equal(AgentRunStatus.Queued, closed.Status);
        Assert.Equal(AgentRunApprovalDecision.Expired, closed.Approval!.Decision);
        Assert.False(closed.Approval.Consumed);
        Assert.False(closed.HasLiveClaim);
    }

    [Fact]
    public void Approval_resume_keeps_the_attempt_that_is_already_at_the_budget()
    {
        foreach (var decision in new[] { AgentRunApprovalDecision.Approved, AgentRunApprovalDecision.Rejected })
        {
            var waiting = NewItem(maxAttempts: 1, id: Guid.NewGuid(), sourceId: Guid.NewGuid())
                .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
                .BeginApproval(2, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(1));
            Assert.Equal(1, waiting.AttemptCount);
            var decided = waiting.DecideApproval(ApprovalId, 3, 1, ActionHash, decision, Now.AddSeconds(2));
            var resumed = decided.TakeClaim(GenerationB, Now.AddSeconds(2), Now.AddMinutes(2));
            Assert.Equal(AgentRunStatus.Running, resumed.Status);
            Assert.Equal(1, resumed.AttemptCount);
            Assert.Equal(GenerationB, resumed.Claim!.Generation);
        }

        var expiredWait = NewItem(maxAttempts: 1, id: Guid.NewGuid(), sourceId: Guid.NewGuid())
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .BeginApproval(2, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(1));
        var expired = expiredWait.ExpireApproval(3, Now.AddMinutes(10));
        var resumedAfterExpiry = expired.TakeClaim(GenerationB, Now.AddMinutes(10), Now.AddMinutes(11));
        Assert.Equal(AgentRunStatus.Running, resumedAfterExpiry.Status);
        Assert.Equal(1, resumedAfterExpiry.AttemptCount);

        var retry = NewItem(maxAttempts: 2, id: Guid.NewGuid(), sourceId: Guid.NewGuid())
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .Fail(2, GenerationA, "model-unavailable", "Model timed out.", true, Now.AddSeconds(1), Now.AddSeconds(2));
        var finalAttempt = retry.TakeClaim(GenerationB, Now.AddSeconds(2), Now.AddMinutes(2));
        Assert.Equal(2, finalAttempt.AttemptCount);
        var finalWait = finalAttempt.BeginApproval(
            finalAttempt.Revision,
            GenerationB,
            ApprovalId,
            "demo.sensitive_action",
            "{}",
            ActionHash,
            "Preview",
            Now.AddMinutes(12),
            Now.AddSeconds(3));
        var finalDecision = finalWait.DecideApproval(ApprovalId, finalWait.Revision, 1, ActionHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(4));
        var resumedFinal = finalDecision.TakeClaim(GenerationA, Now.AddSeconds(4), Now.AddMinutes(3));
        Assert.Equal(2, resumedFinal.AttemptCount);
        Assert.Equal(AgentRunStatus.Running, resumedFinal.Status);
    }

    [Fact]
    public void Side_effect_dispatch_requires_the_same_approved_action()
    {
        var prepared = NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1));
        var substituted = Assert.Throws<AgentRunTransitionException>(() =>
            prepared.MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, OtherToolCallId, OtherHash, Now.AddSeconds(2)));
        Assert.Equal(AgentRunTransitionFailure.Rejected, substituted.Failure);
        Assert.Equal(ActionHash, prepared.SideEffect.ActionHash);

        var rejected = prepared
            .BeginApproval(3, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(2))
            .DecideApproval(ApprovalId, 4, 1, ActionHash, AgentRunApprovalDecision.Rejected, Now.AddSeconds(3));
        Assert.Equal(AgentRunSideEffectDisposition.None, rejected.SideEffect.Disposition);
        var rejectedRun = rejected.TakeClaim(GenerationB, Now.AddSeconds(3), Now.AddMinutes(2));
        Assert.Equal(1, rejectedRun.AttemptCount);
        var rejectedDispatch = Assert.Throws<AgentRunTransitionException>(() =>
            rejectedRun.MarkSideEffect(rejectedRun.Revision, GenerationB, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(4)));
        Assert.Equal(AgentRunTransitionFailure.Rejected, rejectedDispatch.Failure);

        var replacementId = Guid.Parse("019944af-0008-7000-8000-0000000000f2");
        var replaced = rejectedRun
            .BeginApproval(rejectedRun.Revision, GenerationB, replacementId, "demo.sensitive_action", "{}", OtherHash, "Send the other note", Now.AddMinutes(11), Now.AddSeconds(4))
            .DecideApproval(replacementId, rejectedRun.Revision + 1, 1, OtherHash, AgentRunApprovalDecision.Approved, Now.AddSeconds(5));
        var replacedRun = replaced.TakeClaim(GenerationA, Now.AddSeconds(5), Now.AddMinutes(3));
        var dispatched = replacedRun
            .MarkSideEffect(replacedRun.Revision, GenerationA, AgentRunSideEffectDisposition.Prepared, OtherToolCallId, OtherHash, Now.AddSeconds(6))
            .MarkSideEffect(replacedRun.Revision + 1, GenerationA, AgentRunSideEffectDisposition.InFlight, OtherToolCallId, OtherHash, Now.AddSeconds(7));
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, dispatched.SideEffect.Disposition);
        Assert.Equal(OtherHash, dispatched.SideEffect.ActionHash);
        Assert.Equal(1, dispatched.AttemptCount);

        var expired = NewItem(id: Guid.Parse("019944af-0008-7000-8000-0000000000c4"), sourceId: Guid.Parse("019944af-0008-7000-8000-0000000000d4"))
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1))
            .BeginApproval(3, GenerationA, ApprovalId, "demo.sensitive_action", "{}", ActionHash, "Preview", Now.AddMinutes(10), Now.AddSeconds(2))
            .ExpireApproval(4, Now.AddMinutes(10));
        Assert.Equal(AgentRunSideEffectDisposition.None, expired.SideEffect.Disposition);
        var expiredRun = expired.TakeClaim(GenerationB, Now.AddMinutes(10), Now.AddMinutes(11));
        var expiredDispatch = Assert.Throws<AgentRunTransitionException>(() =>
            expiredRun.MarkSideEffect(expiredRun.Revision, GenerationB, AgentRunSideEffectDisposition.Succeeded, ToolCallId, ActionHash, Now.AddMinutes(10).AddSeconds(1)));
        Assert.Equal(AgentRunTransitionFailure.Rejected, expiredDispatch.Failure);
    }

    [Fact]
    public void Clearing_a_succeeded_side_effect_preserves_completed_external_effect_summary()
    {
        var claimed = NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1))
            .MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(2))
            .MarkSideEffect(4, GenerationA, AgentRunSideEffectDisposition.Succeeded, ToolCallId, ActionHash, Now.AddSeconds(3));
        var cleared = claimed.ClearSideEffect(5, GenerationA, Now.AddSeconds(4));
        Assert.Equal(AgentRunSideEffectDisposition.None, cleared.SideEffect.Disposition);
        Assert.Equal(AgentRunKnownEffects.ExternalActionCompleted, cleared.KnownEffectSummary);
    }

    [Fact]
    public void Cleared_side_effect_allows_a_second_dispatch_hash_in_the_same_run()
    {
        var claimed = NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now.AddSeconds(1))
            .MarkSideEffect(3, GenerationA, AgentRunSideEffectDisposition.InFlight, ToolCallId, ActionHash, Now.AddSeconds(2))
            .MarkSideEffect(4, GenerationA, AgentRunSideEffectDisposition.Succeeded, ToolCallId, ActionHash, Now.AddSeconds(3))
            .ClearSideEffect(5, GenerationA, Now.AddSeconds(4));
        Assert.Equal(AgentRunSideEffectDisposition.None, claimed.SideEffect.Disposition);
        var next = claimed
            .MarkSideEffect(6, GenerationA, AgentRunSideEffectDisposition.Prepared, OtherToolCallId, OtherHash, Now.AddSeconds(5))
            .MarkSideEffect(7, GenerationA, AgentRunSideEffectDisposition.InFlight, OtherToolCallId, OtherHash, Now.AddSeconds(6));
        Assert.Equal(OtherHash, next.SideEffect.ActionHash);
        Assert.Equal(AgentRunSideEffectDisposition.InFlight, next.SideEffect.Disposition);
    }

    [Fact]
    public void Safe_retry_stops_at_the_attempt_budget()
    {
        var claimed = NewItem(maxAttempts: 2).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        var retrying = claimed.Fail(2, GenerationA, "model-unavailable", "Model timed out.", true, Now.AddSeconds(1), Now.AddSeconds(2));
        Assert.Equal(AgentRunStatus.WaitingToRetry, retrying.Status);
        Assert.False(retrying.HasLiveClaim);
        Assert.Equal("model-unavailable", retrying.Failure!.Code);
        Assert.Equal("Model timed out.", retrying.Failure.Summary);
        var again = retrying.TakeClaim(GenerationB, Now.AddSeconds(2), Now.AddMinutes(2));
        var failedId = Guid.Parse("019944af-0008-7000-8000-0000000000d3");
        var failed = again.Fail(
            again.Revision,
            GenerationB,
            "model-unavailable",
            "Model timed out.",
            true,
            Now.AddSeconds(3),
            Now.AddSeconds(4),
            () => failedId);
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Equal("attempts-exhausted", failed.Failure!.Code);
        Assert.Equal("Retry budget is exhausted. Last attempt: Model timed out.", failed.Failure.Summary);
        Assert.Equal(failedId, failed.Failure.DiagnosticId);
        Assert.DoesNotContain("stack", failed.Failure.Summary, StringComparison.OrdinalIgnoreCase);
        var repeated = failed.Fail(
            1,
            GenerationB,
            "model-unavailable",
            "Model timed out.",
            true,
            Now.AddSeconds(5),
            null,
            () => Guid.Parse("019944af-0008-7000-8000-0000000000d4"));
        Assert.Equal(failed.Revision, repeated.Revision);
        Assert.Equal(failedId, repeated.Failure!.DiagnosticId);
    }

    private static AgentRun CompletedItem() =>
        NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .Complete(2, GenerationA, "Reminder delivered.", Now.AddSeconds(1), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094"));

    private static AgentRunCheckpoint Checkpoint(string payload) => new(payload, 1, 32, 120_000);

    private static AgentRun NewItem(Guid? id = null, Guid? sourceId = null, int maxAttempts = 3,
        ActivationKind kind = ActivationKind.ScheduledWork) =>
        AgentRun.Create(id ?? AgentRunId, new AgentRunOwner(InstanceId, ProfileId),
            new AgentRunAdmission(new Activation(sourceId ?? SourceId,
                Guid.Parse("019944af-0008-7000-8000-000000000092"), kind, [], null,
                sourceId ?? SourceId, null, null, "registration|1|1758600000000", Now),
                "general-assistant", 10, new AgentIdentity("Alex", "Assistant", "Help", "Calm"),
                Guid.Parse("019944af-0008-7000-8000-000000000093")),
            new AgentRunModelPin("synthetic-default", "synthetic", "synthetic-small", null), maxAttempts, Now);
}
