using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Tests;

public sealed class AgentRunContractTests
{
    [Fact]
    public void Deletion_minimizes_a_settled_transcript_but_defers_a_report_body_until_accounted()
    {
        var run = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        run = run.SaveCheckpoint(run.Revision, GenerationA, new("{\"private\":\"private transcript\"}", 3, 40, 900), "private progress", Now);
        run = run.Complete(run.Revision, GenerationA, "private report", Now, outcomeKind: AgentRunOutcomeKind.NoAction);
        var pendingReport = run.RetireConversationContent(Now, preserveResult: true);
        Assert.Equal("private report", pendingReport.Result!.Text);
        Assert.Equal(AgentRun.RetiredConversationCheckpoint, pendingReport.Checkpoint!.PayloadJson);
        Assert.Equal(3, pendingReport.Checkpoint.StepCount); Assert.Equal(900, pendingReport.Checkpoint.RemainingOverallBudgetMs);
        Assert.Same(run.Admission, pendingReport.Admission); Assert.Same(run.PinnedModel, pendingReport.PinnedModel);
        var accounted = pendingReport.RetireConversationContent(Now);
        Assert.Equal(AgentRun.RetiredConversationText, accounted.Result!.Text);
        Assert.Equal(run.Result!.OutcomeKind, accounted.Result.OutcomeKind);
        Assert.Equal(run.Result.CompletedAtUtc, accounted.Result.CompletedAtUtc);
        Assert.Same(accounted, accounted.RetireConversationContent(Now.AddDays(1)));
    }

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

    [Theory]
    [InlineData("valid", true)] [InlineData("ordinary", false)] [InlineData("missing_target", false)]
    [InlineData("negative_boundary", false)] [InlineData("text_boundary", false)] [InlineData("no_requester", false)]
    public void Input_free_approval_resume_requires_structural_configuration_boundary(string scenario, bool accepted)
    {
        var session = Guid.NewGuid(); var root = Guid.NewGuid();
        var values = new Dictionary<string, object?> { ["EnvironmentKind"] = scenario == "ordinary" ? "approval" : "configuration",
            ["ContinuationTarget"] = scenario == "missing_target" ? "" : "workspace.write",
            ["InputBoundarySequence"] = scenario == "text_boundary" ? "7" : scenario == "negative_boundary" ? -1L : 7L };
        var activation = new Activation(GenerationA, session, ActivationKind.ApprovalContinuation, [], Guid.NewGuid(), null, session, SourceId,
            "approval-continuation:" + ApprovalId.ToString("D"), Now, root, GenerationB, JsonSerializer.Serialize(values));
        var context = new TrustedExecutionContext(Guid.NewGuid(), Guid.NewGuid(), scenario == "no_requester" ? null : Guid.NewGuid(), session,
            AgentRunId, GenerationA, root, SourceId, GenerationB, ExecutionOrigin.ApprovalContinuation);
        AgentRunAdmission Build() => new(activation, "general-assistant", 1, NewItem().PinnedPersona, Guid.NewGuid(), AgentRunOutputContract.ConversationResponse,
            executionContext: context, approvalResume: new(SourceId, ApprovalId, Guid.NewGuid(), ActionHash, []));
        if (accepted) Assert.Empty(Build().ApprovalResume!.SourceInputEntryIds);
        else Assert.Throws<ArgumentException>(Build);
    }

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
        Assert.Equal("synthetic", item.PinnedModel.ConnectionId);
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

        foreach (var readOnlyTool in new[] { "browser.navigate", "browser.get_config" })
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Durable_approval_reference_releases_claim_and_resumes_without_spending_an_attempt(int maxAttempts)
    {
        var run = TenantItem(maxAttempts).TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        if (maxAttempts == 2)
            run = run.Fail(run.Revision, GenerationA, "model-unavailable", "Try again", true, Now.AddSeconds(1), Now.AddSeconds(2))
                .TakeClaim(GenerationB, Now.AddSeconds(2), Now.AddMinutes(1));
        var generation = run.Claim!.Generation;
        var waiting = run.WaitForDurableApproval(run.Revision, ApprovalId, generation, Now.AddSeconds(3));
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Null(waiting.Claim);
        Assert.Equal(ApprovalId, waiting.ApprovalRequestId);
        Assert.Throws<AgentRunTransitionException>(() => waiting.TakeClaim(GenerationA, Now.AddMinutes(10), Now.AddMinutes(11)));
        Assert.Throws<AgentRunTransitionException>(() => waiting.ResumeDurableApproval(waiting.Revision, Guid.NewGuid(), Now.AddMinutes(10)));
        var resumed = waiting.ResumeDurableApproval(waiting.Revision, ApprovalId, Now.AddMinutes(10))
            .TakeClaim(GenerationA, Now.AddMinutes(10), Now.AddMinutes(11));
        Assert.Equal(maxAttempts, resumed.AttemptCount);
        Assert.Equal(run.AgentRunId, resumed.AgentRunId);
        Assert.Equal(AgentRunStatus.Running, resumed.Status);
        Assert.Null(typeof(AgentRun).GetProperty("Approval"));
        Assert.Null(typeof(AgentRun).GetMethod("BeginApproval"));
        Assert.Null(typeof(AgentRun).Assembly.GetType("AgentCore.Domain.Conversation.AgentRunApproval"));
    }

    [Fact]
    public void Unowned_run_cannot_enter_the_durable_approval_state()
    {
        var run = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1));
        Assert.Throws<AgentRunTransitionException>(() => run.WaitForDurableApproval(run.Revision, ApprovalId, GenerationA, Now));
    }

    [Fact]
    public void Prepared_side_effect_cannot_dispatch_a_substituted_operation()
    {
        var prepared = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .MarkSideEffect(2, GenerationA, AgentRunSideEffectDisposition.Prepared, ToolCallId, ActionHash, Now);
        Assert.Throws<AgentRunTransitionException>(() => prepared.MarkSideEffect(prepared.Revision, GenerationA,
            AgentRunSideEffectDisposition.InFlight, OtherToolCallId, OtherHash, Now));
        Assert.Equal(ActionHash, prepared.SideEffect.ActionHash);
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

    [Theory]
    [InlineData(8, 1)]
    [InlineData(3, 300)]
    public void Durable_waits_enforce_cumulative_count_and_time_without_spending_attempts(int waits, int seconds)
    {
        var run = NewItem().TakeClaim(GenerationA, Now, Now.AddMinutes(1)); var at = Now;
        for (var index = 0; index < waits; index++)
        {
            var generation = run.Claim!.Generation;
            run = run.SuspendForSignal(run.Revision, generation, Checkpoint("{}"),
                new($"wait-{index}", AgentRunWaitMode.Duration, AgentRunWaitUntil.All, at, at.AddSeconds(seconds)), at);
            Assert.Equal(generation, run.Wait!.SuspendedGeneration); Assert.Null(run.Claim);
            at = at.AddSeconds(seconds);
            run = run.ResumeFromSignal(run.Revision, Guid.NewGuid(), Checkpoint("{}"), at, at.AddMinutes(1));
            Assert.Equal(1, run.AttemptCount);
        }
        Assert.Equal(waits, run.WaitCount); Assert.Equal(waits * seconds, run.TotalWaitSeconds);
        Assert.Throws<AgentRunTransitionException>(() => run.SuspendForSignal(run.Revision, run.Claim!.Generation, Checkpoint("{}"),
            new("over-budget", AgentRunWaitMode.Duration, AgentRunWaitUntil.All, at, at.AddSeconds(1)), at));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(301)]
    public void Wait_descriptor_rejects_invalid_duration_and_duplicate_targets(int seconds)
    {
        Assert.Throws<ArgumentException>(() => new AgentRunWait("wait", AgentRunWaitMode.Duration, AgentRunWaitUntil.All, Now, Now.AddSeconds(seconds)).Validate());
        var target = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => new AgentRunWait("wait", AgentRunWaitMode.Reserved1, AgentRunWaitUntil.Any, Now, Now.AddSeconds(1)).Validate());
    }

    private static AgentRun TenantItem(int maxAttempts)
    {
        var template = NewItem(); var tenant = Guid.NewGuid(); var agent = Guid.NewGuid();
        var activation = template.Admission.Activation;
        var context = new TrustedExecutionContext(tenant, agent, null, template.SessionId,
            template.AgentRunId, activation.ActivationId, activation.RootActivationId, activation.SourceAgentRunId,
            activation.ParentActivationId, ExecutionOrigin.ScheduledOccurrence);
        return AgentRun.Create(template.AgentRunId, new AgentRunOwner(InstanceId, null, tenant, agent),
            new AgentRunAdmission(activation, template.DefinitionId, template.DefinitionVersion, template.PinnedPersona,
                template.ResponseId, template.Admission.OutputContract, executionContext: context), template.PinnedModel, maxAttempts, Now);
    }

    private static AgentRun CompletedItem() =>
        NewItem()
            .TakeClaim(GenerationA, Now, Now.AddMinutes(1))
            .Complete(2, GenerationA, "Reminder delivered.", Now.AddSeconds(1), outcomeEntryId: Guid.Parse("019944af-0008-7000-8000-000000000094"));

    private static AgentRunCheckpoint Checkpoint(string payload) => new(payload, 1, 32, 120_000);

    private static AgentRun NewItem(Guid? id = null, Guid? sourceId = null, int maxAttempts = 3,
        ActivationKind kind = ActivationKind.ScheduledWork) =>
        AgentRun.Create(id ?? AgentRunId, new AgentRunOwner(InstanceId, ProfileId),
            new AgentRunAdmission(Activation.Start(sourceId ?? SourceId,
                Guid.Parse("019944af-0008-7000-8000-000000000092"), kind, [], null,
                sourceId ?? SourceId, null, null, "registration|1|1758600000000", Now),
                "general-assistant", 10, new AgentIdentity("Alex", "Assistant", "Help", "Calm"),
                Guid.Parse("019944af-0008-7000-8000-000000000093"), AgentRunOutputContract.BackgroundOutcome),
            new AgentRunModelPin("synthetic-default", "synthetic", "synthetic-small", null), maxAttempts, Now);
}
