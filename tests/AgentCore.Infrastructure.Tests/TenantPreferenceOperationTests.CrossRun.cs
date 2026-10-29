using System.Text.Json.Nodes;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantPreferenceOperationTests
{
    private static async Task<(Environment Environment, AgentRun Waiting, TenantApprovalRequest Request, ApprovalProcessedSignal Signal)> CrossRunSetup(Fixture f, bool review = false)
    {
        var setup = await Setup(f, permissions: review, mode: HarnessManagementMode.Assisted); var run = setup.Run;
        var call = new ModelToolCall("cross-run-preferences", ToolCatalog.RequestAuthorization, "{\"operation\":\"update_preferences\",\"preferences\":" + Preferences + "}");
        run = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Checkpoint(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation,
                new(AgentRunToolCallCheckpoint.Write([new(ModelRole.Assistant, "", ToolCalls: [call])]), 1, 0, 9000, 100), null), f.Ids.NewId());
        var service = Approvals(f);
        var request = (await service.PreparePreferencesAsync(new(setup.Tenant, run.AgentRunId), TenantPreferenceToolContract.Parameters(TenantPreferenceToolContract.Parse(Preferences)), run.Revision, run.Claim!.Generation)).Request!;
        await service.DecideAsync(setup.Tenant, request.RequestId, setup.Owner, true, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, request.Revision);
        var waiting = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        return (setup, waiting, (await ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, request.RequestId))!, Assert.Single(await ((ITenantApprovalStore)f.Iam).PendingApprovalSignalsAsync(100)));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Atomic_handoff_keeps_root_response_budget_inputs_and_exact_grant_then_replays_after_recovery(bool sqlite, bool review)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await CrossRunSetup(f, review);
        var owner = setup.Environment.Owner; var tenant = setup.Request.TenantId; var source = setup.Waiting;
        var assignmentsBefore = (await f.Iam.ReadTenantAsync(tenant))!.Assignments.OrderBy(a => a.AssignmentId).ToArray();
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, source.SessionId))!;
        var later = await f.RunService().SubmitTextAsync(tenant, source.SessionId, owner, f.Ids.NewId(), "Later independent input", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, session.Snapshot.Revision);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var laterRun = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, later.RunId))!;
        laterRun = await runtime.ApplyTenantRuntimeCommandAsync(laterRun.Owner, laterRun.AgentRunId,
            new AgentRunCommand.Claim(laterRun.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var laterFrame = new TenantRuntimeFrame(laterRun.Owner, laterRun.SessionId, laterRun.AgentRunId, laterRun.Claim!.Generation);
        var laterSnapshot = (await runtime.LoadTenantRuntimeAsync(laterFrame))!;
        var laterResponse = new ConversationEntry(f.Ids.NewId(), laterSnapshot.LastEntrySequence + 1, null, ConversationRole.Assistant, "Later independent response", laterRun.ResponseId,
            EntryStatus.Completed, SessionMode.Text, 0, 26, clock.GetUtcNow(), CompletedAt: clock.GetUtcNow());
        await runtime.SaveTenantRuntimeAsync(laterFrame, laterSnapshot with { Revision = laterSnapshot.Revision + 1, Entries = [.. laterSnapshot.Entries, laterResponse], LastEntrySequence = laterResponse.Sequence }, laterSnapshot.Revision);
        await runtime.ApplyTenantRuntimeCommandAsync(laterRun.Owner, laterRun.AgentRunId,
            new AgentRunCommand.Complete(laterRun.Revision, clock.GetUtcNow(), laterRun.Claim.Generation, "Later independent response", AgentRunOutcomeKind.Response, laterResponse.EntryId), f.Ids.NewId());
        var store = (ITenantApprovalStore)f.Iam; var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        Assert.True(await store.TryResumeApprovalAsync(setup.Signal, clock.GetUtcNow(), continuation: ids));
        Assert.False(await store.TryResumeApprovalAsync(setup.Signal, clock.GetUtcNow(), continuation: new(f.Ids.NewId(), f.Ids.NewId())));
        var retired = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, source.AgentRunId))!;
        Assert.Equal(AgentRunOutcomeKind.Continued, retired.Result!.OutcomeKind); Assert.Equal(ids.RunId, retired.Result.ContinuationRunId); Assert.Null(retired.Result.OutcomeEntryId);
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, ids.RunId))!;
        Assert.Empty(next.Admission.Activation.SourceEntryIds); Assert.Equal(source.Admission.Activation.SourceEntryIds, next.Admission.ApprovalResume!.SourceInputEntryIds);
        Assert.Equal(source.ResponseId, next.ResponseId); Assert.Equal(source.AttemptCount, next.AttemptCount);
        Assert.Equal(9000, next.Checkpoint!.RemainingOverallBudgetMs); Assert.Equal(100, next.Checkpoint.ActiveExecutionMs);
        Assert.Equal(source.Admission.ExecutionContext!.RootActivationId, next.Admission.ExecutionContext!.RootActivationId);
        Assert.Equal(source.AgentRunId, next.Admission.ExecutionContext.ParentRunId); Assert.Equal(owner.PrincipalId, next.Admission.ExecutionContext.OriginalInitiatorPrincipalId);
        next = await runtime.ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId, new AgentRunCommand.Claim(next.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var frame = new TenantRuntimeFrame(next.Owner, next.SessionId, next.AgentRunId, next.Claim!.Generation);
        var snapshot = (await runtime.LoadTenantRuntimeAsync(frame))!;
        Assert.DoesNotContain(snapshot.Entries, e => e.EntryId == later.EntryId); Assert.Single(snapshot.Entries);
        Assert.DoesNotContain((await runtime.ReadTenantRuntimeHistoryAsync(frame, null, null, 100))!.Items, e => e.EntryId == laterResponse.EntryId);
        await runtime.SaveTenantRuntimeAsync(frame, snapshot with { Revision = snapshot.Revision + 1, Summary = "Scoped continuation checkpoint" }, snapshot.Revision);
        if (f.Contexts is { } contexts)
        {
            await using var db = await contexts.CreateDbContextAsync();
            Assert.Equal(source.ActivationId.ToString("D"), (await db.ActivationSourceEntries.SingleAsync(e => e.EntryId == source.Admission.Activation.SourceEntryIds.Single().ToString("D"))).ActivationId);
            Assert.True(await db.Entries.AnyAsync(e => e.EntryId == later.EntryId.ToString("D")));
            Assert.True(await db.Entries.AnyAsync(e => e.EntryId == laterResponse.EntryId.ToString("D")));
        }
        else Assert.NotNull(((InMemoryMemoryStore)f.Memory).FindEntry(source.SessionId, later.EntryId));
        var effects = ApprovedEffects(f); var request = setup.Request;
        var receipts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => effects.ExecuteAsync(new(tenant, next.AgentRunId), request.RequestId, request.Operation.OperationId, next.Claim.Generation, request.Operation.ActionHash).AsTask()));
        Assert.Equal(receipts[0], receipts[1]); Assert.Equal(next.AgentRunId, receipts[0].RunId); Assert.Equal(request.RequestId, receipts[0].RequestId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => effects.ExecuteAsync(new(tenant, source.AgentRunId), request.RequestId, request.Operation.OperationId, source.Claim?.Generation ?? f.Ids.NewId(), request.Operation.ActionHash).AsTask());
        clock.Advance(TimeSpan.FromMinutes(6));
        IIamStore reopened = sqlite ? new SqliteIamStore(f.Contexts!, clock: clock) : f.Iam;
        var current = (await ((ITenantRunStore)reopened).FindTenantRunAsync(tenant, next.AgentRunId))!; var storage = (ITenantRuntimeStore)reopened;
        var recovered = await storage.ApplyTenantRuntimeCommandAsync(current.Owner, current.AgentRunId, new AgentRunCommand.Recover(current.Revision, clock.GetUtcNow()), f.Ids.NewId());
        var claimed = await storage.ApplyTenantRuntimeCommandAsync(recovered.Owner, recovered.AgentRunId, new AgentRunCommand.Claim(recovered.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var continuation = new TenantRunContinuationService((ITenantRunStore)reopened, (ITenantApprovalStore)reopened, ApprovedEffects(f, reopened), clock, storage, f.Ids);
        await Assert.ThrowsAsync<AccessDeniedException>(() => continuation.ResumeAsync(next).AsTask());
        Assert.Equal(TenantOperationOutcome.Completed, (await continuation.ResumeAsync(claimed)).Outcome!.Outcome);
        Assert.Single(await reopened.ReadAuditAsync(tenant, 200), a => a.Action == "operations.preferences.apply");
        Assert.Single(await reopened.ReadAuditAsync(tenant, 200), a => a.Action == "agent-runs.approval-continuation");
        Assert.Equal(3, (await ((ITenantAgentStore)reopened).FindTenantAgentAsync(tenant, next.AgentInstanceId))!.Instance.Revision);
        if (!review) Assert.Equal(assignmentsBefore, (await reopened.ReadTenantAsync(tenant))!.Assignments.OrderBy(a => a.AssignmentId).ToArray());
    }

    [Theory]
    [InlineData(false, "compete")] [InlineData(true, "compete")]
    [InlineData(false, "collision")] [InlineData(true, "collision")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    [InlineData(false, "checkpoint")] [InlineData(true, "checkpoint")]
    public async Task Signal_handoff_has_one_winner_and_failed_admission_never_partially_retires_the_source(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await CrossRunSetup(f); var source = setup.Waiting; var tenant = setup.Request.TenantId;
        var store = (ITenantApprovalStore)f.Iam; var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        if (boundary == "compete")
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () => await store.TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), continuation: new(f.Ids.NewId(), f.Ids.NewId())))));
            Assert.Single(results, success => success);
            Assert.Equal(2, (await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(tenant, source.SessionId)).Count);
            Assert.Empty(await store.PendingApprovalSignalsAsync(100));
            Assert.Single(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == "agent-runs.approval-continuation");
            return;
        }
        if (boundary == "collision")
        {
            var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, source.SessionId))!;
            var other = await f.RunService().SubmitTextAsync(tenant, source.SessionId, setup.Environment.Owner, f.Ids.NewId(), "Independent user root", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, session.Snapshot.Revision);
            ids = ids with { RunId = other.RunId };
        }
        if (boundary == "checkpoint")
        {
            var row = AgentRunStoreMapping.ToRecord(source); var json = JsonNode.Parse(row.PayloadJson)!; json["checkpoint"]!["payloadJson"] = "[]"; row.PayloadJson = json.ToJsonString();
            await SaveRawRun(f, row);
        }
        using var cancellation = new CancellationTokenSource(); if (boundary == "cancel") cancellation.Cancel();
        var success = false; var error = await Record.ExceptionAsync(async () => success = await store.TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), cancellation.Token, ids));
        Assert.False(success); Assert.True(error is null or SecurityConflictException or OperationCanceledException, error?.ToString());
        Assert.Equal(AgentRunStatus.WaitingForApproval, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, source.AgentRunId))!.Status);
        Assert.Single(await store.PendingApprovalSignalsAsync(100));
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(tenant, 200), a => a.Action == "agent-runs.approval-continuation");
        if (f.Contexts is { } contexts) { await using var db = await contexts.CreateDbContextAsync(); Assert.False(await db.Activations.AnyAsync(a => a.ActivationId == ids.ActivationId.ToString("D"))); }
        else Assert.False(((InMemoryMemoryStore)f.Memory).AgentRuns.Activations.ContainsKey(ids.ActivationId));
    }

    [Theory]
    [InlineData(false, "hash")] [InlineData(true, "hash")]
    [InlineData(false, "operation")] [InlineData(true, "operation")]
    [InlineData(false, "emptyCheckpoint")] [InlineData(true, "emptyCheckpoint")]
    [InlineData(false, "missingCheckpoint")] [InlineData(true, "missingCheckpoint")]
    public async Task Stored_continuation_metadata_cannot_substitute_another_exact_operation_before_effect(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await CrossRunSetup(f); var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), continuation: ids));
        var next = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Request.TenantId, ids.RunId))!;
        var row = AgentRunStoreMapping.ToRecord(next); var json = JsonNode.Parse(row.PayloadJson)!;
        if (boundary == "emptyCheckpoint") json["checkpoint"]!["payloadJson"] = "[]";
        else if (boundary == "missingCheckpoint") json["checkpoint"] = null;
        else json["admission"]!["approvalResume"]![boundary == "hash" ? "actionHash" : "operationId"] = boundary == "hash" ? new string('0', 64) : f.Ids.NewId().ToString("D");
        row.PayloadJson = json.ToJsonString();
        await SaveRawRun(f, row);
        async Task Attempt()
        {
            var claimed = await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(next.Owner, next.AgentRunId,
                new AgentRunCommand.Claim(next.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            if (boundary.EndsWith("Checkpoint", StringComparison.Ordinal))
                await ApprovedEffects(f).ExecuteAsync(new(setup.Request.TenantId, next.AgentRunId), setup.Request.RequestId, setup.Request.Operation.OperationId, claimed.Claim!.Generation, setup.Request.Operation.ActionHash);
        }
        var error = await Assert.ThrowsAsync<SecurityConflictException>(Attempt);
        Assert.Equal("approval_continuation_mismatch", error.ReasonCode);
        Assert.Equal(2, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(setup.Request.TenantId, next.AgentInstanceId))!.Instance.Revision);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Request.TenantId, setup.Request.Operation.OperationId));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task Cancellation_of_source_or_successor_does_not_revive_or_consume_the_approved_operation(bool sqlite, bool successor)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await CrossRunSetup(f); var run = setup.Waiting; var tenant = setup.Request.TenantId;
        if (successor)
        {
            var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
            Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), continuation: ids));
            run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, ids.RunId))!;
            await f.RunService().CancelAsync(tenant, run.AgentRunId, setup.Environment.Owner, (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, run.Revision);
        }
        else
        {
            await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
            Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), continuation: new(f.Ids.NewId(), f.Ids.NewId())));
            Assert.Single(await ((ITenantRunStore)f.Iam).ListTenantRunIdsAsync(tenant, run.SessionId));
        }
        var current = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, run.AgentRunId))!; Assert.Equal(AgentRunStatus.Cancelled, current.Status);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovedEffects(f).ExecuteAsync(new(tenant, run.AgentRunId), setup.Request.RequestId, setup.Request.Operation.OperationId, f.Ids.NewId(), setup.Request.Operation.ActionHash).AsTask());
        Assert.Equal(2, (await ((ITenantAgentStore)f.Iam).FindTenantAgentAsync(tenant, run.AgentInstanceId))!.Instance.Revision);
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(tenant, setup.Request.Operation.OperationId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Equal_time_queue_order_matches_across_stores_without_assigning_input_ownership_to_a_continuation(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T08:00:00Z"));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await CrossRunSetup(f); var tenant = setup.Request.TenantId;
        var session = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, setup.Waiting.SessionId))!;
        var later = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, setup.Environment.Owner, f.Ids.NewId(), "Independent queued input", (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision, session.Snapshot.Revision);
        var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, clock.GetUtcNow(), continuation: ids));
        var queued = await ((ITenantRuntimeStore)f.Iam).ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100);
        // Admission preserves both facts; only the first queued input may own the Session.
        Assert.Equal(new[] { later.RunId }, queued.Select(r => r.AgentRunId));
        var runs = (ITenantRunStore)f.Iam;
        var first = (await runs.FindTenantRunAsync(tenant, later.RunId))!;
        await ((ITenantRuntimeStore)f.Iam).ApplyTenantRuntimeCommandAsync(first.Owner, first.AgentRunId,
            new AgentRunCommand.RequestCancellation(first.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
        var successor = (await runs.FindTenantRunAsync(tenant, ids.RunId))!;
        Assert.Empty(successor.Admission.Activation.SourceEntryIds);
        Assert.Equal(new[] { ids.RunId }, (await ((ITenantRuntimeStore)f.Iam).ListRunnableTenantRunsAsync(clock.GetUtcNow(), 100)).Select(r => r.AgentRunId));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Known_unaccepted_dispatch_preserves_the_last_attempt_and_does_not_spend_waiting_time(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow.AddSeconds(1));
        await using var f = await Fixture.Create(sqlite, clock); var setup = await CrossRunSetup(f);
        var sourceRow = AgentRunStoreMapping.ToRecord(setup.Waiting); var json = JsonNode.Parse(sourceRow.PayloadJson)!;
        json["maxAttempts"] = 1; sourceRow.PayloadJson = json.ToJsonString(); await SaveRawRun(f, sourceRow);
        var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, clock.GetUtcNow(), continuation: ids));
        var runtime = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var run = (await runs.FindTenantRunAsync(setup.Request.TenantId, ids.RunId))!;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
            Assert.Equal(1, run.AttemptCount); Assert.False(run.Checkpoint!.AwaitingRuntimeAdmission);
            clock.Advance(TimeSpan.FromSeconds(30));
            run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.DeferApprovalDispatch(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, clock.GetUtcNow().AddSeconds(1)), f.Ids.NewId(), runtimeGeneration: run.Claim.Generation);
            Assert.Equal(9000, run.Checkpoint!.RemainingOverallBudgetMs); Assert.Equal(100, run.Checkpoint.ActiveExecutionMs);
            Assert.True(run.Checkpoint.AwaitingRuntimeAdmission); Assert.Null(run.Checkpoint.BudgetRecordedAtUtc); Assert.Equal(1, run.AttemptCount);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var receipt = await ApprovedEffects(f).ExecuteAsync(new(setup.Request.TenantId, run.AgentRunId), setup.Request.RequestId, setup.Request.Operation.OperationId, run.Claim!.Generation, setup.Request.Operation.ActionHash);
        Assert.Equal(TenantOperationOutcome.Completed, receipt.Outcome); Assert.Equal(1, run.AttemptCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Authenticated_Run_cancel_also_stops_a_quiescent_capacity_retry(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await CrossRunSetup(f); var ids = new TenantApprovalContinuationIds(f.Ids.NewId(), f.Ids.NewId());
        Assert.True(await ((ITenantApprovalStore)f.Iam).TryResumeApprovalAsync(setup.Signal, f.Clock.GetUtcNow(), continuation: ids));
        var runtime = (ITenantRuntimeStore)f.Iam; var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Request.TenantId, ids.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.DeferApprovalDispatch(run.Revision, f.Clock.GetUtcNow(), run.Claim!.Generation, f.Clock.GetUtcNow().AddSeconds(5)), f.Ids.NewId());
        var cancelled = await f.RunService().CancelAsync(setup.Request.TenantId, run.AgentRunId, setup.Environment.Owner, (await f.Iam.ReadTenantAsync(setup.Request.TenantId))!.Tenant.Revision, run.Revision);
        Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status);
        await Assert.ThrowsAsync<AccessDeniedException>(() => ApprovedEffects(f).ExecuteAsync(new(setup.Request.TenantId, run.AgentRunId), setup.Request.RequestId, setup.Request.Operation.OperationId, f.Ids.NewId(), setup.Request.Operation.ActionHash).AsTask());
        Assert.Null(await ((ITenantApprovedOperationStore)f.Iam).FindOperationReceiptAsync(setup.Request.TenantId, setup.Request.Operation.OperationId));
    }

    private static async Task SaveRawRun(Fixture f, AgentRunRecord row)
    {
        if (f.Contexts is { } contexts)
        { await using var db = await contexts.CreateDbContextAsync(); var saved = await db.AgentRuns.SingleAsync(r => r.AgentRunId == row.AgentRunId); saved.PayloadJson = row.PayloadJson; await db.SaveChangesAsync(); }
        else { var memory = (InMemoryMemoryStore)f.Memory; lock (memory.AdmissionGate) memory.AgentRuns.Runs[Guid.Parse(row.AgentRunId)] = AgentRunStoreMapping.ToDomain(row); }
    }
}
