using System.Data;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Work;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class WorkItemStoreContractTests
{
    private static readonly Guid InstanceA = Guid.Parse("019944af-0009-7000-8000-0000000000a1");
    private static readonly Guid ProfileA = Guid.Parse("019944af-0009-7000-8000-0000000000b1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-0009-7000-8000-0000000000a2");
    private static readonly Guid ProfileB = Guid.Parse("019944af-0009-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 2, 0, 0, TimeSpan.Zero);
    private const string ActionHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    private const string ToolCallId = "tool-call-a";
    private const string OtherToolCallId = "tool-call-b";

    [Fact]
    public async Task Latest_registration_read_is_owner_scoped_and_not_limited_by_the_recent_page()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var registration = Id(8000);
            await store.CreateAsync(NewItem(owner, Id(1), Id(501), Now, registrationId: registration));
            await store.CreateAsync(NewItem(owner, Id(2), Id(502), Now, registrationId: registration));
            for (var index = 3; index <= 105; index++)
                await store.CreateAsync(NewItem(owner, Id(index), Id(index + 500), Now.AddMinutes(index)));
            Assert.DoesNotContain(await store.ListAsync(owner, 100), item => item.Provenance.RegistrationId == registration);
            Assert.Equal(Id(2), (await store.GetLatestForRegistrationAsync(owner, registration))!.WorkItemId);
            Assert.Null(await store.GetLatestForRegistrationAsync(new(InstanceB, ProfileA), registration));
            Assert.Null(await store.GetLatestForRegistrationAsync(new(InstanceA, ProfileB), registration));
            Assert.Null(await store.GetLatestForRegistrationAsync(owner, Id(8001)));
        });
    }

    [Fact]
    public async Task Pages_reach_old_work_with_timestamp_ties_and_reject_foreign_cursors()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            for (var index = 1; index <= 125; index++)
                await store.CreateAsync(NewItem(owner, Id(index), Id(index + 500), Now));
            var seen = new List<Guid>();
            Guid? before = null;
            while (true)
            {
                var page = await store.ListPageAsync(owner, 20, before, false);
                if (page.Count == 0) break;
                seen.AddRange(page.Select(item => item.WorkItemId));
                before = page[^1].WorkItemId;
            }
            Assert.Equal(125, seen.Count);
            Assert.Equal(125, seen.Distinct().Count());
            Assert.Equal(Id(125), seen[0]);
            Assert.Equal(Id(1), seen[^1]);
            Assert.Empty(await store.ListPageAsync(owner, 20, null, true));
            var foreign = new WorkOwner(InstanceB, ProfileB);
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => store.ListPageAsync(foreign, 20, before, false).AsTask());
            Assert.Equal("NotFound", error.Code);
        });
    }

    [Fact]
    public async Task Create_is_owner_scoped_and_idempotent_for_one_source_occurrence()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var other = new WorkOwner(InstanceB, ProfileB);
            var sourceId = Guid.Parse("019944af-0009-7000-8000-0000000000d1");
            var firstId = Guid.Parse("019944af-0009-7000-8000-0000000000c1");
            var secondId = Guid.Parse("019944af-0009-7000-8000-0000000000c2");
            var created = await store.CreateAsync(NewItem(owner, firstId, sourceId, Now));
            var duplicate = await store.CreateAsync(NewItem(owner, secondId, sourceId, Now.AddSeconds(1)));
            Assert.Equal(WorkItemCreateKind.Created, created.Kind);
            Assert.Equal(WorkItemCreateKind.Existing, duplicate.Kind);
            Assert.Equal(firstId, created.Item.WorkItemId);
            Assert.Equal(firstId, duplicate.Item.WorkItemId);
            Assert.Equal(WorkSourceKind.ApplicationEvent, (await store.GetAsync(owner, firstId))!.Provenance.SourceKind);

            var results = await Task.WhenAll(
                store.CreateAsync(NewItem(owner, Guid.Parse("019944af-0009-7000-8000-0000000000c3"), sourceId, Now)).AsTask(),
                store.CreateAsync(NewItem(owner, Guid.Parse("019944af-0009-7000-8000-0000000000c4"), sourceId, Now)).AsTask());
            Assert.All(results, result => Assert.Equal(firstId, result.Item.WorkItemId));
            Assert.Single(await store.ListAsync(owner, 10));
            Assert.Empty(await store.ListAsync(other, 10));
            Assert.Null(await store.GetAsync(other, firstId));
            Assert.Equal(firstId, (await store.GetBySourceOccurrenceAsync(sourceId))!.WorkItemId);

            var hidden = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.RequestCancellationAsync(other, firstId, 1, null, Now.AddMinutes(1)).AsTask());
            Assert.Equal("NotFound", hidden.Code);
            Assert.DoesNotContain("SECRET_EVIDENCE", hidden.Message, StringComparison.Ordinal);

            var foreign = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.CreateAsync(NewItem(other, Guid.Parse("019944af-0009-7000-8000-0000000000c5"), sourceId, Now)).AsTask());
            Assert.Equal("Conflict", foreign.Code);
            Assert.DoesNotContain("SECRET_EVIDENCE", foreign.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Stale_revision_and_generation_cannot_checkpoint_or_complete()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var item = await store.CreateAsync(NewItem(owner, Id(1), Id(11), Now));
            var generation = Id(21);
            var claimed = await store.TryClaimAsync(item.Item.WorkItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            Assert.Equal(2, claimed.Revision);
            var saved = await store.CheckpointAsync(
                claimed.WorkItemId,
                2,
                generation,
                new WorkCheckpoint("SECRET_CHECKPOINT", 1, 16, 1000),
                "Delivering reminder",
                Now.AddSeconds(2));
            var stale = await Assert.ThrowsAsync<AgentCoreException>(() => store.CheckpointAsync(
                saved.WorkItemId,
                2,
                generation,
                new WorkCheckpoint("other", 1, 16, 1000),
                null,
                Now.AddSeconds(3)).AsTask());
            Assert.Equal("Conflict", stale.Code);
            Assert.Equal(3, (await store.GetAsync(owner, saved.WorkItemId))!.Revision);

            var completed = await store.CompleteAsync(saved.WorkItemId, 3, generation, "Reminder delivered.", Now.AddSeconds(3));
            Assert.Equal(WorkItemStatus.Completed, completed.Status);
            Assert.Null(completed.Claim);
            var repeated = await store.CompleteAsync(saved.WorkItemId, 4, generation, "Reminder delivered.", Now.AddSeconds(4));
            Assert.Equal(completed.Revision, repeated.Revision);
            Assert.Null(await store.TryClaimAsync(saved.WorkItemId, Id(22), Now.AddSeconds(5), Now.AddMinutes(2)));
            var terminal = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.RequestCancellationAsync(owner, saved.WorkItemId, repeated.Revision, null, Now.AddSeconds(5)).AsTask());
            Assert.Equal("ValidationError", terminal.Code);
            var loaded = await store.GetAsync(owner, saved.WorkItemId);
            Assert.Equal("Reminder delivered.", loaded!.Result!.Text);
            Assert.Equal("SECRET_CHECKPOINT", loaded.Checkpoint!.PayloadJson);
            Assert.DoesNotContain("SECRET_CHECKPOINT", loaded.ToPublicSummary().ResultText, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Claim_expiry_recovery_rejects_the_old_generation()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var created = await store.CreateAsync(NewItem(owner, Id(2), Id(12), Now, maxAttempts: 2));
            var generation = Id(23);
            var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, Now, Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var retried = await store.RecoverExpiredClaimsAsync(Now.AddMinutes(1));
            Assert.Equal(1, retried.RecoveredCount);
            Assert.Empty(retried.TerminalFailures);
            var recovered = await store.GetAsync(owner, created.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.WaitingToRetry, recovered!.Status);
            Assert.Null(recovered.Claim);
            var stale = await Assert.ThrowsAsync<AgentCoreException>(() => store.CheckpointAsync(
                recovered.WorkItemId,
                recovered.Revision,
                generation,
                new WorkCheckpoint("{}", 0, 0, 1),
                null,
                Now.AddMinutes(2)).AsTask());
            Assert.Equal("Conflict", stale.Code);

            var next = await store.TryClaimAsync(recovered.WorkItemId, Id(24), Now.AddMinutes(1), Now.AddMinutes(2));
            Assert.NotNull(next);
            Assert.Equal(2, next.AttemptCount);
            var terminal = await store.RecoverExpiredClaimsAsync(Now.AddMinutes(2));
            Assert.Equal(1, terminal.RecoveredCount);
            var exhausted = await store.GetAsync(owner, created.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.Failed, exhausted!.Status);
            Assert.Equal("attempts-exhausted", exhausted.Failure!.Code);
            var reported = Assert.Single(terminal.TerminalFailures);
            Assert.Equal(exhausted.WorkItemId, reported.WorkItemId);
            Assert.Equal(exhausted.Failure.DiagnosticId, reported.Failure!.DiagnosticId);
            Assert.NotEqual(Guid.Empty, exhausted.Failure.DiagnosticId);
            Assert.Null(await store.TryClaimAsync(exhausted.WorkItemId, Id(25), Now.AddMinutes(3), Now.AddMinutes(4)));
            Assert.Empty(await store.ListRunnableAsync(Now.AddMinutes(3), 10));
        });
    }

    [Fact]
    public async Task Cancellation_survives_and_blocks_completion()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var created = await store.CreateAsync(NewItem(owner, Id(3), Id(13), Now));
            var queued = await store.RequestCancellationAsync(owner, created.Item.WorkItemId, 1, null, Now.AddSeconds(1));
            Assert.Equal(WorkItemStatus.Cancelled, queued.Status);
            var repeated = await store.RequestCancellationAsync(owner, queued.WorkItemId, 1, null, Now.AddSeconds(2));
            Assert.Equal(queued.Revision, repeated.Revision);

            var running = await store.CreateAsync(NewItem(owner, Id(4), Id(14), Now.AddSeconds(1)));
            var generation = Id(26);
            var claimed = await store.TryClaimAsync(running.Item.WorkItemId, generation, Now.AddSeconds(2), Now.AddMinutes(2));
            Assert.NotNull(claimed);
            var requested = await store.RequestCancellationAsync(owner, claimed.WorkItemId, claimed.Revision, "Effect unknown.", Now.AddSeconds(3));
            var blocked = await Assert.ThrowsAsync<AgentCoreException>(() =>
                store.CompleteAsync(claimed.WorkItemId, requested.Revision, generation, "Done.", Now.AddSeconds(4)).AsTask());
            Assert.Equal("Conflict", blocked.Code);
            var cancelled = await store.CommitCancellationAsync(claimed.WorkItemId, requested.Revision, generation, null, Now.AddSeconds(4));
            Assert.Equal(WorkItemStatus.Cancelled, cancelled.Status);
            Assert.Equal("Effect unknown.", cancelled.KnownEffectSummary);
            Assert.Null(cancelled.Claim);
            var loaded = await store.GetAsync(owner, cancelled.WorkItemId);
            Assert.Equal(WorkItemStatus.Cancelled, loaded!.Status);
            Assert.Contains(loaded.WorkItemId, (await store.ListAsync(owner, 10)).Select(item => item.WorkItemId));
        });
    }

    [Fact]
    public async Task Approval_wait_round_trips_and_accepts_one_exact_decision()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var other = new WorkOwner(InstanceB, ProfileB);
            var created = await store.CreateAsync(NewItem(owner, Id(5), Id(15), Now));
            var generation = Id(27);
            var approvalId = Id(37);
            var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var waiting = await store.BeginApprovalAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                approvalId,
                "demo.sensitive_action",
                """{"body":"SECRET_ACTION"}""",
                ActionHash,
                "Send the weekly note",
                Now.AddMinutes(10),
                Now.AddSeconds(2));
            Assert.Equal(WorkItemStatus.WaitingForApproval, waiting.Status);
            Assert.Null(waiting.Claim);
            var loaded = await store.GetAsync(owner, waiting.WorkItemId);
            Assert.Equal("Send the weekly note", loaded!.Approval!.Preview);
            Assert.Equal("""{"body":"SECRET_ACTION"}""", loaded.Approval.PreparedActionJson);
            Assert.DoesNotContain("SECRET_ACTION", loaded.ToPublicSummary().ApprovalPreview, StringComparison.Ordinal);
            Assert.Empty(await store.ListRunnableAsync(Now.AddSeconds(3), 10));

            var mismatch = await Assert.ThrowsAsync<AgentCoreException>(() => store.DecideApprovalAsync(
                owner,
                waiting.WorkItemId,
                approvalId,
                waiting.Revision,
                waiting.Approval!.Revision,
                OtherHash,
                WorkApprovalDecision.Approved,
                Now.AddSeconds(3)).AsTask());
            Assert.Equal("Conflict", mismatch.Code);
            var hidden = await Assert.ThrowsAsync<AgentCoreException>(() => store.DecideApprovalAsync(
                other,
                waiting.WorkItemId,
                approvalId,
                waiting.Revision,
                1,
                ActionHash,
                WorkApprovalDecision.Approved,
                Now.AddSeconds(3)).AsTask());
            Assert.Equal("NotFound", hidden.Code);

            var approved = await store.DecideApprovalAsync(
                owner,
                waiting.WorkItemId,
                approvalId,
                waiting.Revision,
                waiting.Approval!.Revision,
                ActionHash,
                WorkApprovalDecision.Approved,
                Now.AddSeconds(3));
            Assert.Equal(WorkItemStatus.Queued, approved.Status);
            Assert.True(approved.Approval!.Consumed);
            Assert.Equal(waiting.WorkItemId, approved.WorkItemId);
            var again = await store.DecideApprovalAsync(
                owner,
                approved.WorkItemId,
                approvalId,
                approved.Revision,
                approved.Approval.Revision,
                ActionHash,
                WorkApprovalDecision.Approved,
                Now.AddSeconds(4));
            Assert.Equal(approved.Revision, again.Revision);
            Assert.Equal(approved.WorkItemId, Assert.Single(await store.ListRunnableAsync(Now.AddSeconds(4), 10)).WorkItemId);
        });
    }

    [Fact]
    public async Task Approval_resume_claims_the_same_attempt_at_the_budget_limit()
    {
        await ForEachStore(async store =>
        {
            await AssertApprovalResumeAsync(store, 1, 0, WorkApprovalDecision.Approved, 40);
            await AssertApprovalResumeAsync(store, 1, 0, WorkApprovalDecision.Rejected, 50);
            await AssertApprovalResumeAsync(store, 1, 0, WorkApprovalDecision.Expired, 60);
            await AssertApprovalResumeAsync(store, 3, 2, WorkApprovalDecision.Approved, 70);
            await AssertApprovalResumeAsync(store, 3, 2, WorkApprovalDecision.Rejected, 80);
            await AssertApprovalResumeAsync(store, 3, 2, WorkApprovalDecision.Expired, 90);
        });
    }

    [Fact]
    public async Task Side_effect_dispatch_follows_the_approved_action()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var substituted = await store.CreateAsync(NewItem(owner, Id(100), Id(101), Now));
            var substitutedGeneration = Id(102);
            var substitutedClaim = await store.TryClaimAsync(substituted.Item.WorkItemId, substitutedGeneration, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(substitutedClaim);
            var prepared = await store.MarkSideEffectAsync(
                substitutedClaim.WorkItemId,
                substitutedClaim.Revision,
                substitutedGeneration,
                WorkSideEffectDisposition.Prepared,
                ToolCallId,
                ActionHash,
                Now.AddSeconds(2));
            var swapped = await Assert.ThrowsAsync<AgentCoreException>(() => store.MarkSideEffectAsync(
                prepared.WorkItemId,
                prepared.Revision,
                substitutedGeneration,
                WorkSideEffectDisposition.InFlight,
                OtherToolCallId,
                OtherHash,
                Now.AddSeconds(3)).AsTask());
            Assert.Equal("Conflict", swapped.Code);
            var unchanged = await store.GetAsync(owner, prepared.WorkItemId);
            Assert.Equal(WorkSideEffectDisposition.Prepared, unchanged!.SideEffect.Disposition);
            Assert.Equal(ActionHash, unchanged.SideEffect.ActionHash);

            var rejected = await RejectedDispatchItemAsync(store, owner, 110, WorkApprovalDecision.Rejected);
            var rejectedDispatch = await Assert.ThrowsAsync<AgentCoreException>(() => store.MarkSideEffectAsync(
                rejected.WorkItemId,
                rejected.Revision,
                rejected.Claim!.Generation,
                WorkSideEffectDisposition.InFlight,
                ToolCallId,
                ActionHash,
                rejected.UpdatedAtUtc.AddSeconds(1)).AsTask());
            Assert.Equal("Conflict", rejectedDispatch.Code);
            Assert.Equal(WorkSideEffectDisposition.None, (await store.GetAsync(owner, rejected.WorkItemId))!.SideEffect.Disposition);

            var expired = await RejectedDispatchItemAsync(store, owner, 120, WorkApprovalDecision.Expired);
            var expiredDispatch = await Assert.ThrowsAsync<AgentCoreException>(() => store.MarkSideEffectAsync(
                expired.WorkItemId,
                expired.Revision,
                expired.Claim!.Generation,
                WorkSideEffectDisposition.Succeeded,
                ToolCallId,
                ActionHash,
                expired.UpdatedAtUtc.AddSeconds(1)).AsTask());
            Assert.Equal("Conflict", expiredDispatch.Code);
            Assert.Equal(WorkSideEffectDisposition.None, (await store.GetAsync(owner, expired.WorkItemId))!.SideEffect.Disposition);
        });
    }

    [Fact]
    public async Task Legacy_side_effect_rows_without_tool_call_id_survive_sqlite_reopen()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var store = new SqliteWorkItemStore(factory);
            var owner = new WorkOwner(InstanceA, ProfileA);
            var workItemId = Id(200);
            var sourceId = Id(201);
            var generation = Id(202);
            var approvalId = Id(203);
            var created = await store.CreateAsync(NewItem(owner, workItemId, sourceId, Now));
            var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var checkpoint = new WorkCheckpoint(
                DurableToolCallCheckpoint.Write(
                [
                    new ModelMessage(
                        ModelRole.Assistant,
                        string.Empty,
                        ToolCalls:
                        [
                            new ModelToolCall("h1", "http.request", """{"method":"POST","url":"https://example.com/items","body":"FIRST"}"""),
                            new ModelToolCall("h2", "http.request", """{"method":"POST","url":"https://example.com/items/2","body":"SECOND"}""")
                        ])
                ]),
                1,
                32,
                1000);
            var checkpointed = await store.CheckpointAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                checkpoint,
                "Waiting",
                Now.AddSeconds(2));
            var prepared = await store.MarkSideEffectAsync(
                checkpointed.WorkItemId,
                checkpointed.Revision,
                generation,
                WorkSideEffectDisposition.Prepared,
                "h1",
                ActionHash,
                Now.AddSeconds(3));
            var waiting = await store.BeginApprovalAsync(
                prepared.WorkItemId,
                prepared.Revision,
                generation,
                approvalId,
                "http.request",
                """{"body":"FIRST"}""",
                ActionHash,
                "POST https://example.com/items",
                Now.AddMinutes(10),
                Now.AddSeconds(4));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE WorkItems
                    SET SideEffectToolCallId = NULL,
                        CheckpointJson = 'SECRET_CHECKPOINT'
                    WHERE WorkItemId = $id;
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$id";
                parameter.Value = workItemId.ToString("D");
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync();
            }

            var legacyPending = await store.GetAsync(owner, waiting.WorkItemId);
            Assert.Equal(WorkItemStatus.WaitingForApproval, legacyPending!.Status);
            Assert.Equal(WorkSideEffectDisposition.Prepared, legacyPending.SideEffect.Disposition);
            Assert.Null(legacyPending.SideEffect.ToolCallId);

            await using (var commandConnection = new SqliteConnection($"Data Source={path}"))
            {
                await commandConnection.OpenAsync();
                await using var inFlight = commandConnection.CreateCommand();
                inFlight.CommandText =
                    """
                    UPDATE WorkItems
                    SET Status = 1,
                        SideEffectDisposition = 2,
                        SideEffectToolCallId = NULL,
                        CurrentApprovalId = NULL,
                        ClaimGeneration = $generation,
                        ClaimedAtUtc = $claimedAt,
                        ClaimLeaseExpiresAtUtc = $leaseExpiresAt,
                        CheckpointJson = $checkpoint
                    WHERE WorkItemId = $id;
                    """;
                inFlight.Parameters.AddWithValue("$id", workItemId.ToString("D"));
                inFlight.Parameters.AddWithValue("$generation", generation.ToString("D"));
                inFlight.Parameters.AddWithValue("$claimedAt", Now.AddSeconds(1).ToUnixTimeMilliseconds());
                inFlight.Parameters.AddWithValue("$leaseExpiresAt", Now.AddMinutes(1).ToUnixTimeMilliseconds());
                inFlight.Parameters.AddWithValue("$checkpoint", checkpoint.PayloadJson);
                await inFlight.ExecuteNonQueryAsync();
            }

            var legacyUncertain = await store.GetAsync(owner, workItemId);
            Assert.Equal(WorkItemStatus.Running, legacyUncertain!.Status);
            Assert.Equal(WorkSideEffectDisposition.Indeterminate, legacyUncertain.SideEffect.Disposition);
            Assert.Null(legacyUncertain.SideEffect.ToolCallId);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Legacy_succeeded_without_tool_call_id_binds_by_action_hash_not_first_result()
    {
        const string argsA =
            """{"method":"POST","url":"https://example.com/items/a","body":"FIRST"}""";
        const string argsB =
            """{"method":"POST","url":"https://example.com/items/b","body":"SECOND"}""";
        var hashB = ToolActionHash.Compute(
            ToolCatalog.HttpRequest,
            JsonSerializer.Deserialize<JsonElement>(argsB));
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var store = new SqliteWorkItemStore(factory);
            var owner = new WorkOwner(InstanceA, ProfileA);
            var workItemId = Id(210);
            var sourceId = Id(211);
            var generation = Id(212);
            var created = await store.CreateAsync(NewItem(owner, workItemId, sourceId, Now));
            var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var checkpoint = new WorkCheckpoint(
                DurableToolCallCheckpoint.Write(
                [
                    new ModelMessage(
                        ModelRole.Assistant,
                        string.Empty,
                        ToolCalls:
                        [
                            new ModelToolCall("a", ToolCatalog.HttpRequest, argsA),
                            new ModelToolCall("b", ToolCatalog.HttpRequest, argsB)
                        ]),
                    new(ModelRole.Tool, """{"ok":true}""", ToolCallId: "a", Name: ToolCatalog.HttpRequest)
                ]),
                2,
                32,
                1000);
            var checkpointed = await store.CheckpointAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                checkpoint,
                "Running",
                Now.AddSeconds(2));
            var prepared = await store.MarkSideEffectAsync(
                checkpointed.WorkItemId,
                checkpointed.Revision,
                generation,
                WorkSideEffectDisposition.Prepared,
                "b",
                hashB,
                Now.AddSeconds(3));
            var inFlight = await store.MarkSideEffectAsync(
                prepared.WorkItemId,
                prepared.Revision,
                generation,
                WorkSideEffectDisposition.InFlight,
                "b",
                hashB,
                Now.AddSeconds(4));
            await store.MarkSideEffectAsync(
                inFlight.WorkItemId,
                inFlight.Revision,
                generation,
                WorkSideEffectDisposition.Succeeded,
                "b",
                hashB,
                Now.AddSeconds(5));

            await using (var db = await factory.CreateDbContextAsync())
            {
                var connection = db.Database.GetDbConnection();
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync();
                }

                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE WorkItems
                    SET SideEffectToolCallId = NULL
                    WHERE WorkItemId = $id;
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "$id";
                parameter.Value = workItemId.ToString("D");
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync();
            }

            var legacy = await store.GetAsync(owner, workItemId);
            Assert.Equal(WorkSideEffectDisposition.Succeeded, legacy!.SideEffect.Disposition);
            Assert.Equal("b", legacy.SideEffect.ToolCallId);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Unsafe_side_effect_recovery_does_not_requeue()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var created = await store.CreateAsync(NewItem(owner, Id(6), Id(16), Now));
            var generation = Id(28);
            var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, Now, Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var prepared = await store.MarkSideEffectAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                WorkSideEffectDisposition.Prepared,
                ToolCallId,
                ActionHash,
                Now.AddSeconds(1));
            var inFlight = await store.MarkSideEffectAsync(
                prepared.WorkItemId,
                prepared.Revision,
                generation,
                WorkSideEffectDisposition.InFlight,
                ToolCallId,
                ActionHash,
                Now.AddSeconds(2));
            var terminal = await store.RecoverExpiredClaimsAsync(Now.AddMinutes(1));
            Assert.Equal(1, terminal.RecoveredCount);
            var failed = await store.GetAsync(owner, created.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.Failed, failed!.Status);
            Assert.Equal("side-effect-indeterminate", failed.Failure!.Code);
            Assert.Equal(WorkSideEffectDisposition.Indeterminate, failed.SideEffect.Disposition);
            var reported = Assert.Single(terminal.TerminalFailures);
            Assert.Equal(failed.Failure.DiagnosticId, reported.Failure!.DiagnosticId);
            Assert.NotEqual(Guid.Empty, failed.Failure.DiagnosticId);
            Assert.Empty(await store.ListRunnableAsync(Now.AddMinutes(2), 10));
        });
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_pinned_persona_snapshot()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var owner = new WorkOwner(InstanceA, ProfileA);
            var pinned = new AgentIdentity(
                "Alice",
                "Executive Secretary",
                "Pinned at intake.",
                "Formal");
            var store = new SqliteWorkItemStore(factory);
            var created = await store.CreateAsync(NewItem(owner, Id(20), Id(21), Now, pinnedPersona: pinned));
            var reopened = new SqliteWorkItemStore(Factory(path));
            var loaded = await reopened.GetAsync(owner, created.Item.WorkItemId);
            Assert.NotNull(loaded);
            Assert.Equal(pinned, loaded!.Provenance.PinnedPersona);
            Assert.Equal("Alice", loaded.Provenance.PersonaName);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_cancellation_result_and_approval()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var owner = new WorkOwner(InstanceA, ProfileA);
            var store = new SqliteWorkItemStore(factory);
            var reminder = await store.CreateAsync(NewItem(owner, Id(7), Id(17), Now, kind: WorkSourceKind.Schedule));
            var generation = Id(29);
            var claimed = await store.TryClaimAsync(reminder.Item.WorkItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            await store.CheckpointAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                new WorkCheckpoint("SECRET_CHECKPOINT", 2, 64, 90_000),
                "Delivering reminder",
                Now.AddSeconds(2));
            var completed = await store.CompleteAsync(claimed.WorkItemId, claimed.Revision + 1, generation, "Reminder delivered.", Now.AddSeconds(3));

            var approvalSource = Id(18);
            var pending = await store.CreateAsync(NewItem(owner, Id(8), approvalSource, Now.AddSeconds(1)));
            var approvalGeneration = Id(30);
            var approvalClaim = await store.TryClaimAsync(pending.Item.WorkItemId, approvalGeneration, Now.AddSeconds(2), Now.AddMinutes(2));
            Assert.NotNull(approvalClaim);
            var waiting = await store.BeginApprovalAsync(
                approvalClaim.WorkItemId,
                approvalClaim.Revision,
                approvalGeneration,
                Id(38),
                "demo.sensitive_action",
                """{"body":"SECRET_ACTION"}""",
                ActionHash,
                "Send the weekly note",
                Now.AddMinutes(11),
                Now.AddSeconds(3));

            var cancelClaimed = await store.TryClaimAsync(
                (await store.CreateAsync(NewItem(owner, Id(9), Id(19), Now.AddSeconds(2)))).Item.WorkItemId,
                Id(31),
                Now.AddSeconds(4),
                Now.AddMinutes(3));
            Assert.NotNull(cancelClaimed);
            var cancelRequested = await store.RequestCancellationAsync(owner, cancelClaimed.WorkItemId, cancelClaimed.Revision, null, Now.AddSeconds(5));
            var cancelled = await store.CommitCancellationAsync(cancelClaimed.WorkItemId, cancelRequested.Revision, Id(31), "Effect unknown.", Now.AddSeconds(6));

            var reopened = new SqliteWorkItemStore(Factory(path));
            var loaded = await reopened.GetAsync(owner, completed.WorkItemId);
            Assert.Equal(WorkItemStatus.Completed, loaded!.Status);
            Assert.Equal("Reminder delivered.", loaded.Result!.Text);
            Assert.Equal("SECRET_CHECKPOINT", loaded.Checkpoint!.PayloadJson);
            Assert.Equal(WorkSourceKind.Schedule, loaded.Provenance.SourceKind);
            Assert.Equal("synthetic-small", loaded.Model.ModelId);
            Assert.DoesNotContain("SECRET_EVIDENCE", loaded.ToPublicSummary().ResultText, StringComparison.Ordinal);

            var stillWaiting = await reopened.GetAsync(owner, waiting.WorkItemId);
            Assert.Equal(WorkItemStatus.WaitingForApproval, stillWaiting!.Status);
            Assert.Null(stillWaiting.Claim);
            Assert.Equal("Send the weekly note", stillWaiting.Approval!.Preview);
            Assert.Equal(ActionHash, stillWaiting.Approval.ActionHash);
            Assert.False(stillWaiting.Approval.Consumed);

            var stillCancelled = await reopened.GetAsync(owner, cancelled.WorkItemId);
            Assert.Equal(WorkItemStatus.Cancelled, stillCancelled!.Status);
            Assert.Null(stillCancelled.Claim);
            Assert.Equal("Effect unknown.", stillCancelled.KnownEffectSummary);
        }
        finally
        {
            Release(path);
        }
    }

    [Fact]
    public async Task Migration_creates_unique_source_and_approval_tables()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            await using var db = await factory.CreateDbContextAsync();
            var names = await TableNamesAsync(db);
            Assert.Contains("WorkItems", names);
            Assert.Contains("WorkApprovals", names);
            var indexes = await IndexNamesAsync(db, "WorkItems");
            Assert.Contains("IX_WorkItems_SourceOccurrenceId", indexes);
            Assert.True(await ForeignKeyCountAsync(db, "WorkApprovals") >= 1);
        }
        finally
        {
            Release(path);
        }
    }

    private static async Task AssertApprovalResumeAsync(
        IWorkItemStore store,
        int maxAttempts,
        int retriesBeforeFinalAttempt,
        WorkApprovalDecision decision,
        int idBase)
    {
        var owner = new WorkOwner(InstanceA, ProfileA);
        var createdAt = Now.AddMinutes(idBase);
        var created = await store.CreateAsync(NewItem(owner, Id(idBase), Id(idBase + 1), createdAt, maxAttempts));
        var current = created.Item;
        for (var retry = 0; retry < retriesBeforeFinalAttempt; retry++)
        {
            var generation = Id(idBase + 10 + retry);
            var claimedAt = createdAt.AddSeconds(retry + 1);
            var claimed = await store.TryClaimAsync(current.WorkItemId, generation, claimedAt, claimedAt.AddMinutes(1));
            Assert.NotNull(claimed);
            current = await store.FailAsync(
                claimed.WorkItemId,
                claimed.Revision,
                generation,
                "model-unavailable",
                "Model timed out.",
                true,
                claimedAt.AddMilliseconds(100),
                claimedAt.AddMilliseconds(200));
            Assert.Equal(WorkItemStatus.WaitingToRetry, current.Status);
        }

        var finalGeneration = Id(idBase + 30);
        var finalAt = createdAt.AddSeconds(retriesBeforeFinalAttempt + 1);
        var finalClaim = await store.TryClaimAsync(current.WorkItemId, finalGeneration, finalAt, finalAt.AddMinutes(1));
        Assert.NotNull(finalClaim);
        Assert.Equal(maxAttempts, finalClaim.AttemptCount);
        var waiting = await store.BeginApprovalAsync(
            finalClaim.WorkItemId,
            finalClaim.Revision,
            finalGeneration,
            Id(idBase + 31),
            "demo.sensitive_action",
            "{}",
            ActionHash,
            "Preview",
            finalAt.AddMinutes(10),
            finalAt.AddMilliseconds(100));
        var decidedAt = decision == WorkApprovalDecision.Expired ? finalAt.AddMinutes(10) : finalAt.AddMilliseconds(200);
        var decided = decision == WorkApprovalDecision.Expired
            ? await store.ExpireApprovalAsync(waiting.WorkItemId, waiting.Revision, decidedAt)
            : await store.DecideApprovalAsync(
                owner,
                waiting.WorkItemId,
                waiting.Approval!.ApprovalId,
                waiting.Revision,
                waiting.Approval.Revision,
                ActionHash,
                decision,
                decidedAt);
        Assert.Equal(WorkItemStatus.Queued, decided.Status);
        Assert.Contains(decided.WorkItemId, (await store.ListRunnableAsync(decidedAt, 20)).Select(item => item.WorkItemId));
        var resumed = await store.TryClaimAsync(decided.WorkItemId, Id(idBase + 32), decidedAt, decidedAt.AddMinutes(1));
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemStatus.Running, resumed.Status);
        Assert.Equal(maxAttempts, resumed.AttemptCount);
        Assert.DoesNotContain(resumed.WorkItemId, (await store.ListRunnableAsync(decidedAt, 20)).Select(item => item.WorkItemId));
    }

    private static async Task<WorkItem> RejectedDispatchItemAsync(
        IWorkItemStore store,
        WorkOwner owner,
        int idBase,
        WorkApprovalDecision decision)
    {
        var createdAt = Now.AddHours(1).AddMinutes(idBase);
        var created = await store.CreateAsync(NewItem(owner, Id(idBase), Id(idBase + 1), createdAt));
        var generation = Id(idBase + 2);
        var claimed = await store.TryClaimAsync(created.Item.WorkItemId, generation, createdAt.AddSeconds(1), createdAt.AddMinutes(1));
        Assert.NotNull(claimed);
        var prepared = await store.MarkSideEffectAsync(
            claimed.WorkItemId,
            claimed.Revision,
            generation,
            WorkSideEffectDisposition.Prepared,
            ToolCallId,
            ActionHash,
            createdAt.AddSeconds(2));
        var waiting = await store.BeginApprovalAsync(
            prepared.WorkItemId,
            prepared.Revision,
            generation,
            Id(idBase + 3),
            "demo.sensitive_action",
            "{}",
            ActionHash,
            "Preview",
            createdAt.AddMinutes(10),
            createdAt.AddSeconds(3));
        var decidedAt = decision == WorkApprovalDecision.Expired ? createdAt.AddMinutes(10) : createdAt.AddSeconds(4);
        var decided = decision == WorkApprovalDecision.Expired
            ? await store.ExpireApprovalAsync(waiting.WorkItemId, waiting.Revision, decidedAt)
            : await store.DecideApprovalAsync(
                owner,
                waiting.WorkItemId,
                waiting.Approval!.ApprovalId,
                waiting.Revision,
                waiting.Approval.Revision,
                ActionHash,
                decision,
                decidedAt);
        Assert.Equal(WorkSideEffectDisposition.None, decided.SideEffect.Disposition);
        var resumed = await store.TryClaimAsync(decided.WorkItemId, Id(idBase + 4), decidedAt, decidedAt.AddMinutes(1));
        Assert.NotNull(resumed);
        return resumed;
    }

    [Fact]
    public async Task Quiet_completion_has_no_alert_and_attention_recovery_keeps_one_key()
    {
        await ForEachStore(async store =>
        {
            var owner = new WorkOwner(InstanceA, ProfileA);
            var quietClaim = await ClaimAsync(store, owner, Id(41), Id(42));
            var quiet = await store.CompleteAsync(
                quietClaim.WorkItemId,
                quietClaim.Revision,
                quietClaim.Claim!.Generation,
                "Nothing to report.",
                Now.AddMinutes(1),
                attentionRequired: false);
            Assert.False(quiet.Result!.AttentionRequired);
            Assert.Empty(await store.ListAttentionAlertKeysAsync(quiet.WorkItemId));

            var attentionClaim = await ClaimAsync(store, owner, Id(43), Id(44));
            var attention = await store.CompleteAsync(
                attentionClaim.WorkItemId,
                attentionClaim.Revision,
                attentionClaim.Claim!.Generation,
                "Two orders need review.",
                Now.AddMinutes(2),
                attentionRequired: true);
            var attentionPage = await store.ListPageAsync(owner, 20, null, true);
            Assert.Equal(attention.WorkItemId, Assert.Single(attentionPage).WorkItemId);
            var key = WorkAttentionKey.Format(attention.WorkItemId, attention.Revision);
            Assert.Equal([key], await store.ListAttentionAlertKeysAsync(attention.WorkItemId));
            var recovered = await store.CompleteAsync(
                attention.WorkItemId,
                attention.Revision,
                attentionClaim.Claim.Generation,
                "Two orders need review.",
                Now.AddMinutes(3),
                attentionRequired: true);
            Assert.Equal(attention.Revision, recovered.Revision);
            Assert.False(await store.TryRecordAttentionAlertAsync(attention.WorkItemId, attention.Revision, Now.AddMinutes(4)));
            Assert.Equal([key], await store.ListAttentionAlertKeysAsync(attention.WorkItemId));
        });
    }

    [Fact]
    public async Task Attention_result_survives_sqlite_reopen()
    {
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var owner = new WorkOwner(InstanceA, ProfileA);
            var store = new SqliteWorkItemStore(factory);
            var claimed = await ClaimAsync(store, owner, Id(45), Id(46));
            var completed = await store.CompleteAsync(
                claimed.WorkItemId,
                claimed.Revision,
                claimed.Claim!.Generation,
                "A payment failed.",
                Now.AddMinutes(1),
                attentionRequired: true);
            var key = WorkAttentionKey.Format(completed.WorkItemId, completed.Revision);

            var reopened = new SqliteWorkItemStore(Factory(path));
            var loaded = await reopened.GetAsync(owner, completed.WorkItemId);
            Assert.True(loaded!.Result!.AttentionRequired);
            Assert.Equal("A payment failed.", loaded.Result.Text);
            Assert.Equal([key], await reopened.ListAttentionAlertKeysAsync(completed.WorkItemId));
            Assert.False(await reopened.TryRecordAttentionAlertAsync(completed.WorkItemId, completed.Revision, Now.AddMinutes(2)));
            Assert.Equal([key], await reopened.ListAttentionAlertKeysAsync(completed.WorkItemId));
        }
        finally
        {
            Release(path);
        }
    }

    private static async Task<WorkItem> ClaimAsync(IWorkItemStore store, WorkOwner owner, Guid workItemId, Guid sourceId)
    {
        await store.CreateAsync(NewItem(owner, workItemId, sourceId, Now, kind: WorkSourceKind.Schedule));
        var generation = Id(workItemId.GetHashCode() & 0x7fffffff);
        return (await store.TryClaimAsync(workItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1)))!;
    }

    private static WorkItem NewItem(
        WorkOwner owner,
        Guid workItemId,
        Guid sourceId,
        DateTimeOffset createdAt,
        int maxAttempts = 3,
        WorkSourceKind kind = WorkSourceKind.ApplicationEvent,
        AgentIdentity? pinnedPersona = null,
        Guid? registrationId = null) =>
        WorkItem.Create(
            workItemId,
            owner,
            new WorkProvenance(
                sourceId,
                kind,
                registrationId,
                Guid.Parse("019944af-0009-7000-8000-000000000092"),
                null,
                $"source|{sourceId:N}",
                createdAt,
                createdAt,
                """{"instruction":"SECRET_EVIDENCE"}""",
                "general-assistant",
                10,
                pinnedPersona?.Name ?? "Alex",
                pinnedPersona),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            maxAttempts,
            createdAt);

    private static Guid Id(int value) => Guid.Parse($"019944af-0009-7000-8000-{value:D12}");

    private static async Task ForEachStore(Func<IWorkItemStore, Task> exercise)
    {
        await exercise(new InMemoryWorkItemStore());
        var path = TempDatabase();
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            await exercise(new SqliteWorkItemStore(factory));
        }
        finally
        {
            Release(path);
        }
    }

    private static string TempDatabase() =>
        Path.Combine(Path.GetTempPath(), $"agent-core-work-{Guid.NewGuid():N}.db");

    private static TestFactory Factory(string path) =>
        new(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SqlitePragmaInterceptor(5_000))
            .Options);

    private static void Release(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        SqliteConnection.ClearPool(connection);
        File.Delete(path);
    }

    private static async Task<List<string>> TableNamesAsync(AgentCoreDbContext db)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<string>> IndexNamesAsync(AgentCoreDbContext db, string table)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = $table;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<int> ForeignKeyCountAsync(AgentCoreDbContext db, string table)
    {
        var connection = await OpenAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{table}\");";
        var count = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            count++;
        }

        return count;
    }

    private static async Task<System.Data.Common.DbConnection> OpenAsync(AgentCoreDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        return connection;
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
