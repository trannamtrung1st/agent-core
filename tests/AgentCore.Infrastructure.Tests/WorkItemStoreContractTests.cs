using System.Data;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
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
            Assert.Equal(1, await store.RecoverExpiredClaimsAsync(Now.AddMinutes(1)));
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
            Assert.Equal(1, await store.RecoverExpiredClaimsAsync(Now.AddMinutes(2)));
            var exhausted = await store.GetAsync(owner, created.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.Failed, exhausted!.Status);
            Assert.Equal("attempts-exhausted", exhausted.Failure!.Code);
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
                ActionHash,
                Now.AddSeconds(1));
            var inFlight = await store.MarkSideEffectAsync(
                prepared.WorkItemId,
                prepared.Revision,
                generation,
                WorkSideEffectDisposition.InFlight,
                ActionHash,
                Now.AddSeconds(2));
            Assert.Equal(1, await store.RecoverExpiredClaimsAsync(Now.AddMinutes(1)));
            var failed = await store.GetAsync(owner, created.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.Failed, failed!.Status);
            Assert.Equal(WorkSideEffectDisposition.Indeterminate, failed.SideEffect.Disposition);
            Assert.Empty(await store.ListRunnableAsync(Now.AddMinutes(2), 10));
        });
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

    private static WorkItem NewItem(
        WorkOwner owner,
        Guid workItemId,
        Guid sourceId,
        DateTimeOffset createdAt,
        int maxAttempts = 3,
        WorkSourceKind kind = WorkSourceKind.ApplicationEvent) =>
        WorkItem.Create(
            workItemId,
            owner,
            new WorkProvenance(
                sourceId,
                kind,
                null,
                Guid.Parse("019944af-0009-7000-8000-000000000092"),
                null,
                $"source|{sourceId:N}",
                createdAt,
                createdAt,
                """{"instruction":"SECRET_EVIDENCE"}""",
                "general-assistant",
                10,
                "Alex"),
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
