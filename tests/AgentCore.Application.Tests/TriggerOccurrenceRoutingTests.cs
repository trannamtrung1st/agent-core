using AgentCore.Application.Events;
using AgentCore.Application.Testing;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class TriggerOccurrenceRoutingTests
{
    private static readonly Guid InstanceId = Guid.Parse("019944af-00c1-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00c1-7000-8000-0000000000b1");
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_order_event_routes_once_and_invalid_payloads_do_not()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var ingress = Ingress(harness);
        var router = Router(harness, [runtime.Snapshot.SessionId], runtime);
        var first = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.Parse("019944af-00c2-7000-8000-000000000001"), "A-1", "shipped", "left the dock");
        Assert.Equal(DurableEventOutcome.Admitted, first.Outcome);
        var duplicate = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.Parse("019944af-00c2-7000-8000-000000000001"), "A-1", "shipped", "left the dock");
        Assert.Equal(DurableEventOutcome.Duplicate, duplicate.Outcome);

        await router.RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        var saved = await harness.Store.GetOccurrenceAsync(harness.Owner, first.Occurrence!.OccurrenceId);
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedLive, saved!.Disposition);
        Assert.Equal(OccurrenceAccept.Duplicate, await runtime.SubmitOccurrenceAsync(new OccurrenceDelivery(first.Occurrence.OccurrenceId, harness.Owner, TriggerSourceKind.ApplicationEvent, saved.EvidenceJson)));
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);

        Assert.Equal(DurableEventOutcome.Rejected, (await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "A-2", "refunded", null)).Outcome);
        Assert.Equal(DurableEventOutcome.Rejected, (await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), new string('x', 65), "shipped", null)).Outcome);
        Assert.Equal(DurableEventOutcome.Rejected, (await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "A-3", "shipped", new string('e', 5000))).Outcome);
        Assert.Single(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10));
        Assert.Empty(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Empty(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Rejected, 10));
    }

    [Fact]
    public async Task Missing_owner_does_not_admit_or_hand_off()
    {
        var store = new InMemoryTriggerStore();
        var ingress = new DurableOrderEventIngress(store, Guard(store, includeInstance: false), new SystemIdGenerator(TimeProvider.System), new FakeTimeProvider(Now));
        var result = await ingress.PublishOrderStatusAsync(new TriggerOwner(InstanceId, ProfileId), Guid.NewGuid(), "A-9", "delayed", null);
        Assert.Equal(DurableEventOutcome.Rejected, result.Outcome);
        Assert.Empty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Empty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));
    }

    [Fact]
    public async Task No_or_multiple_runtimes_hand_off_without_a_second_launch()
    {
        var harness = await StartAsync();
        var ingress = Ingress(harness);
        var admitted = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "B-1", "delivered", null);
        var none = Router(harness, [], runtime: null);
        await none.RouteOnceAsync();
        Assert.Equal(OccurrenceRoutingDisposition.AwaitingDurableWork, (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId))!.Disposition);

        var second = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "B-2", "shipped", null);
        var many = Router(harness, [Guid.NewGuid(), Guid.NewGuid()], runtime: null);
        await many.RouteOnceAsync();
        var handed = await harness.Store.GetOccurrenceAsync(harness.Owner, second.Occurrence!.OccurrenceId);
        Assert.Equal(OccurrenceRoutingDisposition.AwaitingDurableWork, handed!.Disposition);
        Assert.Contains("Multiple", handed.DispositionReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_delivery_releases_the_claim_and_a_busy_runtime_queues()
    {
        var harness = await StartAsync();
        var ingress = Ingress(harness);
        var admitted = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "C-1", "shipped", null);
        var failing = new TriggerOccurrenceRouter(
            harness.Store,
            harness.Guard,
            new FixedDirectory([harness.Runtime.Snapshot.SessionId]),
            new UnavailableMailbox(),
            new SystemIdGenerator(TimeProvider.System),
            harness.Time);
        await failing.RouteOnceAsync();
        Assert.Equal(OccurrenceRoutingDisposition.Pending, (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId))!.Disposition);

        await using var runtime = harness.Runtime;
        Assert.True(await runtime.SubmitUserTextAsync("hold the line"));
        await harness.Output.WaitForAsync(item => item.Payload is ResponseStartedOutput);
        var during = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "C-2", "shipped", null);
        var router = Router(harness, [runtime.Snapshot.SessionId], runtime);
        await router.RouteOnceAsync();
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedLive, (await harness.Store.GetOccurrenceAsync(harness.Owner, during.Occurrence!.OccurrenceId))!.Disposition);
        Assert.Equal(1, runtime.Snapshot.Entries.Count(entry => entry.Role == ConversationRole.Assistant));
    }

    [Fact]
    public async Task Ineligible_schedule_is_suspended_without_an_occurrence()
    {
        var store = new InMemoryTriggerStore();
        var owner = new TriggerOwner(InstanceId, ProfileId);
        var due = Now.AddHours(1);
        var registration = new TriggerRegistration(
            Guid.Parse("019944af-00c3-7000-8000-000000000001"),
            owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null);
        await store.CreateAsync(registration);
        var scheduler = new TriggerScheduler(store, NullLogger<TriggerScheduler>.Instance, Guard(store, includeInstance: false));
        var pass = await scheduler.RunOnceAsync(due);
        Assert.Equal(0, pass.Admitted);
        Assert.Equal(TriggerRegistrationStatus.SuspendedPolicy, (await store.GetAsync(owner, registration.RegistrationId))!.Status);
        Assert.Empty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Empty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));
    }

    [Fact]
    public async Task Occurrence_evidence_is_not_a_user_turn_and_email_still_requires_approval()
    {
        const string evidence = "{\"orderReference\":\"A-1\",\"status\":\"shipped\"}";
        var definition = await LoadAsync("customer-support", 2);
        var request = new PromptContextBuilder().Build(
            new AgentContext(
                definition,
                [],
                "",
                null,
                SessionMode.Text,
                null,
                false,
                null,
                new AgentTrigger(Guid.NewGuid(), TriggerKind.ApplicationEvent, evidence)),
            Guid.NewGuid());
        Assert.Contains(request.Messages, message => message.Role == ModelRole.System && message.Text.Contains("Occurrence evidence", StringComparison.Ordinal));
        Assert.DoesNotContain(request.Messages, message => message.Role == ModelRole.User && message.Text.Contains(evidence, StringComparison.Ordinal));
        Assert.Equal(
            TriggerAuthorizationClassification.Occurrence,
            TriggerAuthorization.Classify(TriggerKind.ApplicationEvent, "remind me tomorrow", hasPendingProposal: true));
        var assistant = await LoadAsync("general-assistant", 8);
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(assistant, ToolCatalog.EmailSend, new DelegatingToolConfigurationGate(_ => true)));
    }

    [Fact]
    public async Task Completed_one_shot_routes_once_and_cancel_or_upgrade_does_not_hand_off()
    {
        var harness = await StartAsync("general-assistant", 8);
        await using var runtime = harness.Runtime;
        var owner = harness.Owner;
        var registrationId = Guid.Parse("019944af-00c3-7000-8000-000000000011");
        var due = Now;
        await harness.Store.CreateAsync(new TriggerRegistration(
            registrationId,
            owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new OneShotSchedule(due, "UTC", null, null),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));
        var scheduler = new TriggerScheduler(harness.Store, NullLogger<TriggerScheduler>.Instance, harness.Guard);
        var admitted = await scheduler.RunOnceAsync(due);
        Assert.Equal(1, admitted.Admitted);
        Assert.Equal(TriggerRegistrationStatus.Completed, (await harness.Store.GetAsync(owner, registrationId))!.Status);
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(OccurrenceRoutingDisposition.AcceptedLive, (await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10)).Single().Disposition);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);

        var dailyId = Guid.Parse("019944af-00c3-7000-8000-000000000012");
        var dailyDue = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        await harness.Store.CreateAsync(new TriggerRegistration(
            dailyId,
            owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new DailySchedule(1, new TimeOnly(9, 0), "UTC"),
            dailyDue,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));
        var dailyPass = await scheduler.RunOnceAsync(dailyDue);
        Assert.Equal(1, dailyPass.Admitted);
        var daily = (await harness.Store.GetAsync(owner, dailyId))!;
        await harness.Store.CancelAsync(owner, dailyId, daily.Revision, dailyDue);
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        var cancelled = (await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10))
            .Concat(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Rejected, 10))
            .Single(item => item.RegistrationId == dailyId);
        Assert.Equal(OccurrenceRoutingDisposition.Rejected, cancelled.Disposition);
        Assert.Empty(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));

        var movedId = Guid.Parse("019944af-00c3-7000-8000-000000000013");
        var movedDue = dailyDue.AddDays(1);
        await harness.Store.CreateAsync(new TriggerRegistration(
            movedId,
            owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new DailySchedule(1, new TimeOnly(9, 0), "UTC"),
            movedDue,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));
        Assert.Equal(1, (await scheduler.RunOnceAsync(movedDue)).Admitted);
        var moved = (await harness.Store.GetAsync(owner, movedId))!;
        var later = movedDue.AddHours(2);
        await harness.Store.UpdateAsync(
            owner,
            movedId,
            moved.Revision,
            moved.Intent,
            new DailySchedule(1, new TimeOnly(11, 0), "UTC"),
            later,
            null,
            later);
        Assert.Equal(0, (await scheduler.RunOnceAsync(movedDue)).Admitted);
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(2, (await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10)).Count);

        var support = await StartAsync();
        await using var supportRuntime = support.Runtime;
        var pending = await Ingress(support).PublishOrderStatusAsync(support.Owner, Guid.NewGuid(), "E-1", "delayed", null);
        await support.Instances.UpdateActiveVersionAsync(InstanceId, 1, Now);
        await Router(support, [supportRuntime.Snapshot.SessionId], supportRuntime).RouteOnceAsync();
        Assert.Equal(
            OccurrenceRoutingDisposition.Rejected,
            (await support.Store.GetOccurrenceAsync(support.Owner, pending.Occurrence!.OccurrenceId))!.Disposition);
        Assert.Empty(await support.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AwaitingDurableWork, 10));

        await support.Instances.UpdateActiveVersionAsync(InstanceId, 2, Now);
        var ended = await Ingress(support).PublishOrderStatusAsync(support.Owner, Guid.NewGuid(), "E-2", "shipped", null);
        Assert.True(await supportRuntime.RequestEndAsync());
        await supportRuntime.WaitUntilIdleAsync();
        await Router(support, [supportRuntime.Snapshot.SessionId], supportRuntime).RouteOnceAsync();
        Assert.Equal(
            OccurrenceRoutingDisposition.Pending,
            (await support.Store.GetOccurrenceAsync(support.Owner, ended.Occurrence!.OccurrenceId))!.Disposition);
    }

    [Fact]
    public async Task Pending_occurrence_survives_sqlite_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-route-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options;
        var factory = new SqliteFactory(options);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Now)).EnsureCreatedAsync();
            var store = new SqliteTriggerStore(factory);
            var guard = await GuardAsync(new InMemoryTriggerStore(), includeInstance: true);
            var owner = new TriggerOwner(InstanceId, ProfileId);
            var ingress = new DurableOrderEventIngress(store, guard.Guard, new SystemIdGenerator(TimeProvider.System), new FakeTimeProvider(Now));
            var admitted = await ingress.PublishOrderStatusAsync(owner, Guid.NewGuid(), "R-1", "shipped", null);
            Assert.Equal(DurableEventOutcome.Admitted, admitted.Outcome);

            var reopened = new SqliteTriggerStore(factory);
            var saved = await reopened.GetOccurrenceAsync(owner, admitted.Occurrence!.OccurrenceId);
            Assert.Equal(OccurrenceRoutingDisposition.Pending, saved!.Disposition);
            var router = new TriggerOccurrenceRouter(
                reopened,
                guard.Guard,
                new FixedDirectory([]),
                new UnavailableMailbox(),
                new SystemIdGenerator(TimeProvider.System),
                new FakeTimeProvider(Now));
            await router.RouteOnceAsync();
            Assert.Equal(
                OccurrenceRoutingDisposition.AwaitingDurableWork,
                (await reopened.GetOccurrenceAsync(owner, admitted.Occurrence.OccurrenceId))!.Disposition);
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Detach_and_a_fresh_memory_store_do_not_remove_the_occurrence()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var ingress = Ingress(harness);
        var admitted = await ingress.PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "D-1", "shipped", null);
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        await runtime.DetachAsync();
        Assert.NotNull(await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId));
        Assert.Empty((await new InMemoryMemoryStore().ListCatalogAsync(null, 10, true)).Items);
    }

    private static DurableOrderEventIngress Ingress(Harness harness) =>
        new(harness.Store, harness.Guard, new SystemIdGenerator(TimeProvider.System), harness.Time);

    private static TriggerOccurrenceRouter Router(Harness harness, IReadOnlyList<Guid> sessions, SessionRuntime? runtime) =>
        new(
            harness.Store,
            harness.Guard,
            new FixedDirectory(sessions),
            runtime is null ? new UnavailableMailbox() : new RuntimeMailbox(runtime),
            new SystemIdGenerator(TimeProvider.System),
            harness.Time);

    private static TriggerAdmissionGuard Guard(InMemoryTriggerStore store, bool includeInstance) =>
        GuardAsync(store, includeInstance).GetAwaiter().GetResult().Guard;

    private static async Task<GuardScope> GuardAsync(
        InMemoryTriggerStore store,
        bool includeInstance,
        string definitionId = "customer-support",
        int version = 2)
    {
        _ = store;
        var instances = new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var definitions = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        if (includeInstance)
        {
            var definition = (await definitions.GetAsync(definitionId, version))!;
            await instances.InsertAsync(new AgentInstance(
                InstanceId,
                definition.Id,
                definition.Version,
                definition.Identity,
                AgentInstanceLifecycle.Active,
                Now,
                Now,
                false));
            await memory.SaveProfileAsync(
                new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>
                {
                    ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, Now)
                }, Now),
                0);
        }

        return new GuardScope(new TriggerAdmissionGuard(instances, definitions, memory), instances);
    }

    private static async Task<Harness> StartAsync(string definitionId = "customer-support", int version = 2)
    {
        var definition = await LoadAsync(definitionId, version);
        var time = new FakeTimeProvider(Now);
        var store = new InMemoryTriggerStore();
        var guard = await GuardAsync(store, includeInstance: true, definitionId, version);
        var sessionIds = new DeterministicIdGenerator(
            Enumerable.Range(1, 16).Select(index => Guid.Parse($"019944af-00c4-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf21")]);
        var snapshot = new SessionSnapshot(
            1,
            sessionIds.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: InstanceId);
        var memory = new InMemoryMemoryStore();
        await memory.SaveAsync(snapshot, 0);
        var output = new CapturingSessionOutput();
        var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            memory,
            output,
            sessionIds,
            time,
            NullLogger<SessionRuntime>.Instance);
        await runtime.AttachAsync();
        return new Harness(runtime, store, guard.Guard, guard.Instances, time, new TriggerOwner(InstanceId, ProfileId), output);
    }

    private static async Task<AgentDefinition> LoadAsync(string id, int version)
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync(id, version).ConfigureAwait(false))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed record GuardScope(TriggerAdmissionGuard Guard, InMemoryAgentInstanceStore Instances);

    private sealed record Harness(
        SessionRuntime Runtime,
        InMemoryTriggerStore Store,
        TriggerAdmissionGuard Guard,
        InMemoryAgentInstanceStore Instances,
        FakeTimeProvider Time,
        TriggerOwner Owner,
        CapturingSessionOutput Output);

    private sealed class FixedDirectory(IReadOnlyList<Guid> sessions) : ILiveOccurrenceDirectory
    {
        public IReadOnlyList<LiveOccurrenceTarget> ListCompatible(TriggerOwner owner, TriggerSourceKind sourceKind) =>
            sessions.Select(session => new LiveOccurrenceTarget(session)).ToArray();
    }

    private sealed class RuntimeMailbox(SessionRuntime runtime) : IOccurrenceMailbox
    {
        public Task<OccurrenceAccept> SubmitAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default) =>
            runtime.SubmitOccurrenceAsync(delivery, cancellationToken);
    }

    private sealed class UnavailableMailbox : IOccurrenceMailbox
    {
        public Task<OccurrenceAccept> SubmitAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default) =>
            Task.FromResult(OccurrenceAccept.Unavailable);
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
