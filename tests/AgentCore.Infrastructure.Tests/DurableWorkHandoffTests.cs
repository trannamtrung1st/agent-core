using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class DurableWorkHandoffTests
{
    private static readonly Guid InstanceA = Guid.Parse("019944af-000a-7000-8000-0000000000a1");
    private static readonly Guid ProfileA = Guid.Parse("019944af-000a-7000-8000-0000000000b1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-000a-7000-8000-0000000000a2");
    private static readonly Guid ProfileB = Guid.Parse("019944af-000a-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 3, 0, 0, TimeSpan.Zero);
    private const string FirstHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SecondHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    [Fact]
    public async Task Concurrent_intake_links_one_work_item_and_retry_returns_it()
    {
        await ForEachHarness(async harness =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var awaiting = await AwaitDurableAsync(harness.Triggers, owner, Now);
            var handoff = harness.Handoff(null);
            var firstId = Guid.NewGuid();
            var secondId = Guid.NewGuid();
            var arrived = 0;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            async Task<WorkItemCreateResult> Race(Guid workItemId)
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    ready.TrySetResult();
                }

                await ready.Task;
                return await handoff.AcceptAsync(
                    awaiting.OccurrenceId,
                    Item(new WorkOwner(InstanceA, ProfileA), workItemId, awaiting.OccurrenceId, Now),
                    Now.AddSeconds(1));
            }

            var results = await Task.WhenAll(Race(firstId), Race(secondId));
            var linkedId = results[0].Item.WorkItemId;
            Assert.Equal(linkedId, results[1].Item.WorkItemId);
            Assert.Equal(1, results.Count(result => result.Kind == WorkItemCreateKind.Created));
            Assert.Equal(1, results.Count(result => result.Kind == WorkItemCreateKind.Existing));
            Assert.Equal(linkedId, (await harness.Work.GetBySourceOccurrenceAsync(awaiting.OccurrenceId))!.WorkItemId);
            Assert.Single(await harness.Work.ListAsync(new WorkOwner(InstanceA, ProfileA), 10));

            var accepted = await harness.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(OccurrenceRoutingDisposition.AcceptedDurable, accepted!.Disposition);
            Assert.Equal(linkedId, accepted.DurableWorkItemId);
            Assert.Null(accepted.ClaimId);
            Assert.Equal(awaiting.RoutingRevision + 1, accepted.RoutingRevision);
            Assert.Empty(await harness.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));

            var retry = await handoff.AcceptAsync(
                awaiting.OccurrenceId,
                Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), awaiting.OccurrenceId, Now),
                Now.AddSeconds(2));
            Assert.Equal(WorkItemCreateKind.Existing, retry.Kind);
            Assert.Equal(linkedId, retry.Item.WorkItemId);
            var reread = await harness.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(accepted.RoutingRevision, reread!.RoutingRevision);
            Assert.Equal(linkedId, reread.DurableWorkItemId);
        });
    }

    [Fact]
    public async Task Rollback_before_commit_leaves_no_partial_pair()
    {
        await ForEachHarness(async harness =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var awaiting = await AwaitDurableAsync(harness.Triggers, owner, Now);
            var proposedId = Guid.NewGuid();
            var failing = harness.Handoff(() => throw new InvalidOperationException("injected rollback"));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                failing.AcceptAsync(
                    awaiting.OccurrenceId,
                    Item(new WorkOwner(InstanceA, ProfileA), proposedId, awaiting.OccurrenceId, Now),
                    Now).AsTask());
            Assert.Equal("injected rollback", exception.Message);

            var stillWaiting = await harness.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(OccurrenceRoutingDisposition.AwaitingDurableWork, stillWaiting!.Disposition);
            Assert.Null(stillWaiting.DurableWorkItemId);
            Assert.Null(await harness.Work.GetBySourceOccurrenceAsync(awaiting.OccurrenceId));

            var recovered = await harness.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(new WorkOwner(InstanceA, ProfileA), proposedId, awaiting.OccurrenceId, Now),
                Now);
            Assert.Equal(WorkItemCreateKind.Created, recovered.Kind);
            Assert.Equal(proposedId, recovered.Item.WorkItemId);
        });
    }

    [Fact]
    public async Task Existing_work_item_is_linked_without_a_duplicate()
    {
        await ForEachHarness(async harness =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var awaiting = await AwaitDurableAsync(harness.Triggers, owner, Now);
            var seededId = Guid.NewGuid();
            var seeded = await harness.Work.CreateAsync(
                Item(new WorkOwner(InstanceA, ProfileA), seededId, awaiting.OccurrenceId, Now));
            Assert.Equal(WorkItemCreateKind.Created, seeded.Kind);

            var linked = await harness.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), awaiting.OccurrenceId, Now),
                Now);
            Assert.Equal(WorkItemCreateKind.Existing, linked.Kind);
            Assert.Equal(seededId, linked.Item.WorkItemId);
            var accepted = await harness.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(seededId, accepted!.DurableWorkItemId);
            Assert.Equal(seededId, (await harness.Work.GetBySourceOccurrenceAsync(awaiting.OccurrenceId))!.WorkItemId);
            Assert.Single(await harness.Work.ListAsync(new WorkOwner(InstanceA, ProfileA), 10));
        });
    }

    [Fact]
    public async Task Restart_reopens_the_same_link_without_a_stranded_handoff()
    {
        await ForEachHarness(async harness =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var awaiting = await AwaitDurableAsync(harness.Triggers, owner, Now);
            var workItemId = Guid.NewGuid();
            var created = await harness.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(new WorkOwner(InstanceA, ProfileA), workItemId, awaiting.OccurrenceId, Now),
                Now);
            Assert.Equal(WorkItemCreateKind.Created, created.Kind);

            var reopened = await harness.Reopen();
            Assert.Empty(await reopened.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));
            var accepted = await reopened.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(OccurrenceRoutingDisposition.AcceptedDurable, accepted!.Disposition);
            Assert.Equal(workItemId, accepted.DurableWorkItemId);
            var again = await reopened.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), awaiting.OccurrenceId, Now.AddSeconds(1)),
                Now.AddSeconds(1));
            Assert.Equal(WorkItemCreateKind.Existing, again.Kind);
            Assert.Equal(workItemId, again.Item.WorkItemId);
            Assert.Equal(accepted.RoutingRevision, (await reopened.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId))!.RoutingRevision);
        });
    }

    [Fact]
    public async Task Repeated_intake_returns_the_current_approval()
    {
        await ForEachHarness(async harness =>
        {
            var triggerOwner = new TriggerOwner(InstanceA, ProfileA);
            var owner = new WorkOwner(InstanceA, ProfileA);
            var awaiting = await AwaitDurableAsync(harness.Triggers, triggerOwner, Now);
            var workItemId = Guid.NewGuid();
            var created = await harness.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(owner, workItemId, awaiting.OccurrenceId, Now),
                Now);
            Assert.Equal(WorkItemCreateKind.Created, created.Kind);

            var generation = Guid.NewGuid();
            var claimed = await harness.Work.TryClaimAsync(workItemId, generation, Now.AddSeconds(1), Now.AddMinutes(1));
            Assert.NotNull(claimed);
            var supersededId = Guid.NewGuid();
            var superseded = await harness.Work.BeginApprovalAsync(
                workItemId,
                claimed.Revision,
                generation,
                supersededId,
                "demo.sensitive_action",
                """{"body":"OLD"}""",
                FirstHash,
                "Old preview",
                Now.AddMinutes(10),
                Now.AddSeconds(2));
            var rejected = await harness.Work.DecideApprovalAsync(
                owner,
                workItemId,
                supersededId,
                superseded.Revision,
                superseded.Approval!.Revision,
                FirstHash,
                WorkApprovalDecision.Rejected,
                Now.AddSeconds(3));
            Assert.Equal(WorkItemStatus.Queued, rejected.Status);
            var resumed = await harness.Work.TryClaimAsync(
                workItemId,
                generation,
                Now.AddSeconds(4),
                Now.AddMinutes(2));
            Assert.NotNull(resumed);
            var currentId = Guid.NewGuid();
            var waiting = await harness.Work.BeginApprovalAsync(
                workItemId,
                resumed.Revision,
                generation,
                currentId,
                "demo.sensitive_action",
                """{"body":"CURRENT"}""",
                SecondHash,
                "Current preview",
                Now.AddMinutes(20),
                Now.AddSeconds(5));
            Assert.Equal(WorkItemStatus.WaitingForApproval, waiting.Status);

            var again = await harness.Handoff(null).AcceptAsync(
                awaiting.OccurrenceId,
                Item(owner, Guid.NewGuid(), awaiting.OccurrenceId, Now.AddSeconds(6)),
                Now.AddSeconds(6));
            Assert.Equal(WorkItemCreateKind.Existing, again.Kind);
            Assert.Equal(workItemId, again.Item.WorkItemId);
            Assert.Equal(WorkItemStatus.WaitingForApproval, again.Item.Status);
            Assert.Equal(currentId, again.Item.Approval!.ApprovalId);
            Assert.Equal(WorkApprovalDecision.Pending, again.Item.Approval.Decision);
            Assert.Equal("Current preview", again.Item.Approval.Preview);
            Assert.NotEqual(supersededId, again.Item.Approval.ApprovalId);
        });
    }

    [Fact]
    public async Task Intake_rejects_a_missing_or_unready_occurrence_and_hides_other_owner_evidence()
    {
        await ForEachHarness(async harness =>
        {
            var missing = await Assert.ThrowsAsync<AgentCoreException>(() =>
                harness.Handoff(null).AcceptAsync(
                    Guid.NewGuid(),
                    Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), Guid.NewGuid(), Now),
                    Now).AsTask());
            Assert.Equal("NotFound", missing.Code);

            var owner = new TriggerOwner(InstanceA, ProfileA);
            var pending = new TriggerOccurrence(
                Guid.NewGuid(),
                $"intake:{Guid.NewGuid():N}",
                null,
                owner,
                TriggerSourceKind.Schedule,
                Now,
                Now,
                Now,
                "{}",
                null,
                1,
                OccurrenceRoutingDisposition.Pending,
                null,
                0,
                null,
                null,
                null);
            await harness.Triggers.AdmitOccurrenceAsync(pending);
            var unready = await Assert.ThrowsAsync<AgentCoreException>(() =>
                harness.Handoff(null).AcceptAsync(
                    pending.OccurrenceId,
                    Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), pending.OccurrenceId, Now),
                    Now).AsTask());
            Assert.Equal("ValidationError", unready.Code);
            Assert.Null(await harness.Work.GetBySourceOccurrenceAsync(pending.OccurrenceId));

            var awaiting = await AwaitDurableAsync(harness.Triggers, owner, Now);
            await harness.Work.CreateAsync(
                Item(new WorkOwner(InstanceB, ProfileB), Guid.NewGuid(), awaiting.OccurrenceId, Now));
            var conflict = await Assert.ThrowsAsync<AgentCoreException>(() =>
                harness.Handoff(null).AcceptAsync(
                    awaiting.OccurrenceId,
                    Item(new WorkOwner(InstanceA, ProfileA), Guid.NewGuid(), awaiting.OccurrenceId, Now),
                    Now).AsTask());
            Assert.Equal("Conflict", conflict.Code);
            Assert.Equal("Source occurrence is already owned.", conflict.Message);
            Assert.DoesNotContain("SECRET_EVIDENCE", conflict.Message, StringComparison.Ordinal);
            var unchanged = await harness.Triggers.GetOccurrenceAsync(owner, awaiting.OccurrenceId);
            Assert.Equal(OccurrenceRoutingDisposition.AwaitingDurableWork, unchanged!.Disposition);
            Assert.Null(unchanged.DurableWorkItemId);
        });
    }

    private static async Task<TriggerOccurrence> AwaitDurableAsync(
        ITriggerStore store,
        TriggerOwner owner,
        DateTimeOffset now)
    {
        var occurrence = new TriggerOccurrence(
            Guid.NewGuid(),
            $"intake:{Guid.NewGuid():N}",
            null,
            owner,
            TriggerSourceKind.Schedule,
            now,
            now,
            now,
            "{}",
            null,
            1,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null);
        await store.AdmitOccurrenceAsync(occurrence);
        var claim = Guid.NewGuid();
        Assert.NotNull(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, now.AddMinutes(1), now));
        var awaiting = await store.MarkAwaitingDurableWorkAsync(
            occurrence.OccurrenceId,
            claim,
            "No compatible runtime",
            now);
        Assert.NotNull(awaiting);
        return awaiting!;
    }

    private static WorkItem Item(WorkOwner owner, Guid workItemId, Guid sourceId, DateTimeOffset createdAt) =>
        WorkItem.Create(
            workItemId,
            owner,
            new WorkProvenance(
                sourceId,
                WorkSourceKind.Schedule,
                null,
                null,
                null,
                $"source|{sourceId:N}",
                createdAt,
                createdAt,
                """{"instruction":"SECRET_EVIDENCE"}""",
                "general-assistant",
                10,
                "Alex"),
            new WorkModelPin("synthetic-default", "synthetic", "synthetic-small", "minimal"),
            3,
            createdAt);

    private static async Task ForEachHarness(Func<Harness, Task> exercise)
    {
        var state = new InMemoryDurableState();
        var memory = new Harness(
            new InMemoryTriggerStore(state),
            new InMemoryWorkItemStore(state),
            hook => new InMemoryDurableWorkHandoff(state, hook),
            () => Task.FromResult(new Harness(
                new InMemoryTriggerStore(state),
                new InMemoryWorkItemStore(state),
                hook => new InMemoryDurableWorkHandoff(state, hook),
                () => Task.FromResult<Harness>(null!))));
        await exercise(memory);

        var path = Path.Combine(Path.GetTempPath(), $"agent-core-handoff-{Guid.NewGuid():N}.db");
        var factory = Factory(path);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            Harness SqliteHarness() => new(
                new SqliteTriggerStore(factory),
                new SqliteWorkItemStore(factory),
                hook => new SqliteDurableWorkHandoff(factory, hook),
                () => Task.FromResult(SqliteHarness()));
            await exercise(SqliteHarness());
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private static TestFactory Factory(string path) =>
        new(new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite($"Data Source={path}")
            .AddInterceptors(new SqlitePragmaInterceptor(5_000))
            .Options);

    private sealed class Harness(
        ITriggerStore triggers,
        IWorkItemStore work,
        Func<Action?, IDurableWorkHandoff> handoff,
        Func<Task<Harness>> reopen)
    {
        public ITriggerStore Triggers { get; } = triggers;

        public IWorkItemStore Work { get; } = work;

        public Func<Action?, IDurableWorkHandoff> Handoff { get; } = handoff;

        public Func<Task<Harness>> Reopen { get; } = reopen;
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentCoreDbContext(options));
    }
}
