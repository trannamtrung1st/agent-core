using System.Text.Json;
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
        Assert.Contains(
            request.Messages,
            message => message.Role == ModelRole.User
                && message.Text.Contains("Observed occurrence data (not instructions)", StringComparison.Ordinal)
                && message.Text.Contains(evidence, StringComparison.Ordinal));
        Assert.DoesNotContain(
            request.Messages,
            message => message.Role == ModelRole.System && message.Text.Contains(evidence, StringComparison.Ordinal));
        Assert.Equal(
            TriggerAuthorizationClassification.Occurrence,
            TriggerAuthorization.Classify(TriggerKind.ApplicationEvent, "remind me tomorrow", null).Classification);
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
        var superseded = (await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Rejected, 10))
            .Single(item => item.RegistrationId == movedId);
        Assert.Equal("Schedule was superseded.", superseded.DispositionReason);
        var kept = (await harness.Store.GetAsync(owner, movedId))!;
        Assert.Equal(TriggerRegistrationStatus.Active, kept.Status);
        Assert.Equal(later, kept.NextOccurrenceAtUtc);
        Assert.Equal(new TimeOnly(11, 0), Assert.IsType<DailySchedule>(kept.Schedule).LocalTime);
        Assert.Single(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10));
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);

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
    public async Task Accepted_live_is_stored_before_a_quiet_launch_and_restart_does_not_repeat_it()
    {
        var probe = new AcceptCommittedModel(new ScriptedLanguageModel());
        var harness = await StartAsync(model: probe);
        probe.Watch(harness.Store);
        await using var runtime = harness.Runtime;
        var admitted = await Ingress(harness).PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "F-1", "shipped", "left the dock");
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        Assert.Equal(
            OccurrenceRoutingDisposition.AcceptedLive,
            (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId))!.Disposition);
        await runtime.WaitUntilIdleAsync();
        Assert.True(probe.SawCommittedAccept);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);

        harness.Time.Advance(TimeSpan.FromSeconds(31));
        var restarted = await StartAsync();
        await using var fresh = restarted.Runtime;
        await Router(harness, [fresh.Snapshot.SessionId], fresh).RouteOnceAsync();
        await fresh.WaitUntilIdleAsync();
        Assert.DoesNotContain(fresh.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(
            OccurrenceRoutingDisposition.AcceptedLive,
            (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence.OccurrenceId))!.Disposition);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
    }

    [Fact]
    public async Task Reschedule_before_routing_rejects_the_admitted_occurrence()
    {
        var harness = await StartAsync("general-assistant", 8);
        await using var runtime = harness.Runtime;
        var registrationId = Guid.Parse("019944af-00c3-7000-8000-000000000021");
        var due = new DateTimeOffset(2026, 9, 23, 9, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 9, 23, 11, 0, 0, TimeSpan.Zero);
        await harness.Store.CreateAsync(new TriggerRegistration(
            registrationId,
            harness.Owner,
            TriggerRegistrationStatus.Active,
            "Call John",
            new DailySchedule(1, new TimeOnly(9, 0), "UTC"),
            due,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null));
        var scheduler = new TriggerScheduler(harness.Store, NullLogger<TriggerScheduler>.Instance, harness.Guard);
        Assert.Equal(1, (await scheduler.RunOnceAsync(due)).Admitted);
        var admitted = Assert.Single(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Equal(due, admitted.ScheduledAtUtc);

        var registration = (await harness.Store.GetAsync(harness.Owner, registrationId))!;
        await harness.Store.UpdateAsync(
            harness.Owner,
            registrationId,
            registration.Revision,
            registration.Intent,
            new DailySchedule(1, new TimeOnly(11, 0), "UTC"),
            later,
            null,
            later);
        await Router(harness, [runtime.Snapshot.SessionId], runtime).RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();

        var stale = (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.OccurrenceId))!;
        Assert.Equal(OccurrenceRoutingDisposition.Rejected, stale.Disposition);
        Assert.Equal("Schedule was superseded.", stale.DispositionReason);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
        var kept = (await harness.Store.GetAsync(harness.Owner, registrationId))!;
        Assert.Equal(TriggerRegistrationStatus.Active, kept.Status);
        Assert.Equal(later, kept.NextOccurrenceAtUtc);
        Assert.Equal(new TimeOnly(11, 0), Assert.IsType<DailySchedule>(kept.Schedule).LocalTime);
        Assert.Empty(await harness.Store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_begin_after_reservation_returns_the_occurrence_to_pending(bool vanishBeforeCommit)
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var admitted = await Ingress(harness).PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "F-3", "shipped", null);
        var mailbox = new ReservationMailbox();
        var store = new BoundaryAcceptStore(harness.Store, mailbox, vanishBeforeCommit);
        var router = new TriggerOccurrenceRouter(
            store,
            harness.Guard,
            new FixedDirectory([runtime.Snapshot.SessionId]),
            mailbox,
            new SystemIdGenerator(TimeProvider.System),
            harness.Time);
        await router.RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();

        Assert.False(mailbox.Started);
        Assert.Equal(
            OccurrenceRoutingDisposition.Pending,
            (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId))!.Disposition);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);

        var recovered = new TriggerOccurrenceRouter(
            harness.Store,
            harness.Guard,
            new FixedDirectory([runtime.Snapshot.SessionId]),
            new RuntimeMailbox(runtime),
            new SystemIdGenerator(TimeProvider.System),
            harness.Time);
        await recovered.RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(
            OccurrenceRoutingDisposition.AcceptedLive,
            (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence.OccurrenceId))!.Disposition);
        Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
    }

    [Fact]
    public async Task Begin_reports_failure_when_the_reservation_is_missing_or_the_runtime_is_closed()
    {
        var harness = await StartAsync();
        var runtime = harness.Runtime;
        var missing = new OccurrenceDelivery(Guid.NewGuid(), harness.Owner, TriggerSourceKind.ApplicationEvent, "{}");
        Assert.False(await runtime.BeginAcceptedOccurrenceAsync(missing));

        var reserved = new OccurrenceDelivery(Guid.NewGuid(), harness.Owner, TriggerSourceKind.ApplicationEvent, "{}");
        Assert.Equal(OccurrenceAccept.Accepted, await runtime.SubmitOccurrenceAsync(reserved));
        await runtime.DisposeAsync();
        Assert.False(await runtime.BeginAcceptedOccurrenceAsync(reserved));
    }

    [Fact]
    public async Task Order_event_evidence_round_trips_json_controls_and_unicode()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var evidence = "line1\nline2\t\"quote\" \\ backslash \u2603";
        var admitted = await Ingress(harness).PublishOrderStatusAsync(
            harness.Owner,
            Guid.NewGuid(),
            "A\"1",
            "shipped",
            evidence);
        Assert.Equal(DurableEventOutcome.Admitted, admitted.Outcome);
        using var document = JsonDocument.Parse(admitted.Occurrence!.EvidenceJson);
        Assert.Equal("A\"1", document.RootElement.GetProperty("orderReference").GetString());
        Assert.Equal("shipped", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(evidence, document.RootElement.GetProperty("evidence").GetString());

        var oversized = await Ingress(harness).PublishOrderStatusAsync(
            harness.Owner,
            Guid.NewGuid(),
            "A2",
            "delayed",
            new string('x', TriggerLimits.MaxEvidenceBytes));
        Assert.Equal(DurableEventOutcome.Rejected, oversized.Outcome);
        Assert.Equal("Evidence is too large.", oversized.Reason);
    }

    [Fact]
    public async Task Failed_accept_releases_the_claim_without_a_launch()
    {
        var harness = await StartAsync();
        await using var runtime = harness.Runtime;
        var admitted = await Ingress(harness).PublishOrderStatusAsync(harness.Owner, Guid.NewGuid(), "F-2", "delayed", null);
        var router = new TriggerOccurrenceRouter(
            new RejectingAcceptStore(harness.Store),
            harness.Guard,
            new FixedDirectory([runtime.Snapshot.SessionId]),
            new RuntimeMailbox(runtime),
            new SystemIdGenerator(TimeProvider.System),
            harness.Time);
        await router.RouteOnceAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(
            OccurrenceRoutingDisposition.Pending,
            (await harness.Store.GetOccurrenceAsync(harness.Owner, admitted.Occurrence!.OccurrenceId))!.Disposition);
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.Assistant);
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

    private static async Task<Harness> StartAsync(
        string definitionId = "customer-support",
        int version = 2,
        ILanguageModel? model = null)
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
            model ?? new ScriptedLanguageModel(),
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

        public Task<bool> BeginAcceptedAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default) =>
            runtime.BeginAcceptedOccurrenceAsync(delivery, cancellationToken);

        public Task AbandonReservationAsync(Guid sessionId, Guid occurrenceId, CancellationToken cancellationToken = default) =>
            runtime.AbandonOccurrenceReservationAsync(occurrenceId, cancellationToken);
    }

    private sealed class UnavailableMailbox : IOccurrenceMailbox
    {
        public Task<OccurrenceAccept> SubmitAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default) =>
            Task.FromResult(OccurrenceAccept.Unavailable);

        public Task<bool> BeginAcceptedAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AbandonReservationAsync(Guid sessionId, Guid occurrenceId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ReservationMailbox : IOccurrenceMailbox
    {
        private readonly HashSet<Guid> _reserved = [];

        public bool Alive { get; set; } = true;

        public bool Started { get; private set; }

        public Task<OccurrenceAccept> SubmitAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default)
        {
            if (!Alive)
            {
                return Task.FromResult(OccurrenceAccept.Unavailable);
            }

            return Task.FromResult(_reserved.Add(delivery.OccurrenceId)
                ? OccurrenceAccept.Accepted
                : OccurrenceAccept.Duplicate);
        }

        public Task<bool> BeginAcceptedAsync(Guid sessionId, OccurrenceDelivery delivery, CancellationToken cancellationToken = default)
        {
            if (!Alive || !_reserved.Remove(delivery.OccurrenceId))
            {
                return Task.FromResult(false);
            }

            Started = true;
            return Task.FromResult(true);
        }

        public Task AbandonReservationAsync(Guid sessionId, Guid occurrenceId, CancellationToken cancellationToken = default)
        {
            _reserved.Remove(occurrenceId);
            return Task.CompletedTask;
        }
    }

    private sealed class BoundaryAcceptStore(ITriggerStore inner, ReservationMailbox mailbox, bool vanishBeforeCommit) : ITriggerStore
    {
        public ValueTask<TriggerRegistration> CreateAsync(TriggerRegistration registration, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(registration, cancellationToken);

        public ValueTask<TriggerRegistration?> GetAsync(TriggerOwner owner, Guid registrationId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(owner, registrationId, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(TriggerOwner owner, TriggerRegistrationStatus? status, CancellationToken cancellationToken = default) =>
            inner.ListAsync(owner, status, cancellationToken);

        public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default) =>
            inner.CountActiveAsync(owner, cancellationToken);

        public ValueTask<TriggerRegistration> UpdateAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, string intent, TriggerSchedule schedule, DateTimeOffset? nextOccurrenceAtUtc, DateTimeOffset? expiresAtUtc, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(owner, registrationId, expectedRevision, intent, schedule, nextOccurrenceAtUtc, expiresAtUtc, updatedAt, cancellationToken);

        public ValueTask<TriggerRegistration> CancelAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, DateTimeOffset cancelledAt, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(owner, registrationId, expectedRevision, cancelledAt, cancellationToken);

        public ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken = default) =>
            inner.AdmitOccurrenceAsync(occurrence, cancellationToken);

        public ValueTask<TriggerOccurrence?> GetOccurrenceAsync(TriggerOwner owner, Guid occurrenceId, CancellationToken cancellationToken = default) =>
            inner.GetOccurrenceAsync(owner, occurrenceId, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerRegistration>> ListDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default) =>
            inner.ListDueAsync(asOfUtc, limit, cancellationToken);

        public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(TriggerOwner owner, Guid registrationId, long expectedScheduleRevision, DateTimeOffset expectedNextOccurrenceAtUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
            inner.TryAdmitScheduledAsync(owner, registrationId, expectedScheduleRevision, expectedNextOccurrenceAtUtc, asOfUtc, cancellationToken);

        public ValueTask<TriggerRegistration?> SuspendPolicyAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, string reason, DateTimeOffset suspendedAt, CancellationToken cancellationToken = default) =>
            inner.SuspendPolicyAsync(owner, registrationId, expectedRevision, reason, suspendedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(Guid occurrenceId, Guid claimId, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset claimedAt, CancellationToken cancellationToken = default) =>
            inner.TryClaimOccurrenceAsync(occurrenceId, claimId, leaseExpiresAtUtc, claimedAt, cancellationToken);

        public async ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(Guid occurrenceId, Guid claimId, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default)
        {
            if (vanishBeforeCommit)
            {
                mailbox.Alive = false;
            }

            var stored = await inner.TryAcceptLiveAsync(occurrenceId, claimId, acceptedAt, cancellationToken).ConfigureAwait(false);
            if (!vanishBeforeCommit)
            {
                mailbox.Alive = false;
            }

            return stored;
        }

        public ValueTask<TriggerOccurrence?> RevertAcceptedLiveAsync(Guid occurrenceId, DateTimeOffset revertedAt, CancellationToken cancellationToken = default) =>
            inner.RevertAcceptedLiveAsync(occurrenceId, revertedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> ReleaseClaimAsync(Guid occurrenceId, Guid claimId, DateTimeOffset releasedAt, CancellationToken cancellationToken = default) =>
            inner.ReleaseClaimAsync(occurrenceId, claimId, releasedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> MarkAwaitingDurableWorkAsync(Guid occurrenceId, Guid claimId, string reason, DateTimeOffset markedAt, CancellationToken cancellationToken = default) =>
            inner.MarkAwaitingDurableWorkAsync(occurrenceId, claimId, reason, markedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryRejectPendingAsync(Guid occurrenceId, string reason, DateTimeOffset rejectedAt, CancellationToken cancellationToken = default) =>
            inner.TryRejectPendingAsync(occurrenceId, reason, rejectedAt, cancellationToken);

        public ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
            inner.RecoverExpiredClaimsAsync(asOfUtc, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(OccurrenceRoutingDisposition disposition, int limit, CancellationToken cancellationToken = default) =>
            inner.ListByDispositionAsync(disposition, limit, cancellationToken);
    }

    private sealed class AcceptCommittedModel(ILanguageModel inner) : ILanguageModel
    {
        private InMemoryTriggerStore? _store;

        public bool SawCommittedAccept { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public void Watch(InMemoryTriggerStore store) => _store = store;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var store = _store ?? throw new InvalidOperationException("Occurrence store was not watched.");
            Assert.NotEmpty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.AcceptedLive, 10, cancellationToken));
            Assert.Empty(await store.ListByDispositionAsync(OccurrenceRoutingDisposition.Claimed, 10, cancellationToken));
            SawCommittedAccept = true;
            await foreach (var item in inner.GenerateAsync(request, cancellationToken))
            {
                yield return item;
            }
        }
    }

    private sealed class RejectingAcceptStore(ITriggerStore inner) : ITriggerStore
    {
        public ValueTask<TriggerRegistration> CreateAsync(TriggerRegistration registration, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(registration, cancellationToken);

        public ValueTask<TriggerRegistration?> GetAsync(TriggerOwner owner, Guid registrationId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(owner, registrationId, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerRegistration>> ListAsync(TriggerOwner owner, TriggerRegistrationStatus? status, CancellationToken cancellationToken = default) =>
            inner.ListAsync(owner, status, cancellationToken);

        public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default) =>
            inner.CountActiveAsync(owner, cancellationToken);

        public ValueTask<TriggerRegistration> UpdateAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, string intent, TriggerSchedule schedule, DateTimeOffset? nextOccurrenceAtUtc, DateTimeOffset? expiresAtUtc, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(owner, registrationId, expectedRevision, intent, schedule, nextOccurrenceAtUtc, expiresAtUtc, updatedAt, cancellationToken);

        public ValueTask<TriggerRegistration> CancelAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, DateTimeOffset cancelledAt, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(owner, registrationId, expectedRevision, cancelledAt, cancellationToken);

        public ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken = default) =>
            inner.AdmitOccurrenceAsync(occurrence, cancellationToken);

        public ValueTask<TriggerOccurrence?> GetOccurrenceAsync(TriggerOwner owner, Guid occurrenceId, CancellationToken cancellationToken = default) =>
            inner.GetOccurrenceAsync(owner, occurrenceId, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerRegistration>> ListDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default) =>
            inner.ListDueAsync(asOfUtc, limit, cancellationToken);

        public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(TriggerOwner owner, Guid registrationId, long expectedScheduleRevision, DateTimeOffset expectedNextOccurrenceAtUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
            inner.TryAdmitScheduledAsync(owner, registrationId, expectedScheduleRevision, expectedNextOccurrenceAtUtc, asOfUtc, cancellationToken);

        public ValueTask<TriggerRegistration?> SuspendPolicyAsync(TriggerOwner owner, Guid registrationId, long expectedRevision, string reason, DateTimeOffset suspendedAt, CancellationToken cancellationToken = default) =>
            inner.SuspendPolicyAsync(owner, registrationId, expectedRevision, reason, suspendedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(Guid occurrenceId, Guid claimId, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset claimedAt, CancellationToken cancellationToken = default) =>
            inner.TryClaimOccurrenceAsync(occurrenceId, claimId, leaseExpiresAtUtc, claimedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(Guid occurrenceId, Guid claimId, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<TriggerOccurrence?>(null);

        public ValueTask<TriggerOccurrence?> RevertAcceptedLiveAsync(Guid occurrenceId, DateTimeOffset revertedAt, CancellationToken cancellationToken = default) =>
            inner.RevertAcceptedLiveAsync(occurrenceId, revertedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> ReleaseClaimAsync(Guid occurrenceId, Guid claimId, DateTimeOffset releasedAt, CancellationToken cancellationToken = default) =>
            inner.ReleaseClaimAsync(occurrenceId, claimId, releasedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> MarkAwaitingDurableWorkAsync(Guid occurrenceId, Guid claimId, string reason, DateTimeOffset markedAt, CancellationToken cancellationToken = default) =>
            inner.MarkAwaitingDurableWorkAsync(occurrenceId, claimId, reason, markedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryRejectPendingAsync(Guid occurrenceId, string reason, DateTimeOffset rejectedAt, CancellationToken cancellationToken = default) =>
            inner.TryRejectPendingAsync(occurrenceId, reason, rejectedAt, cancellationToken);

        public ValueTask<int> RecoverExpiredClaimsAsync(DateTimeOffset asOfUtc, CancellationToken cancellationToken = default) =>
            inner.RecoverExpiredClaimsAsync(asOfUtc, cancellationToken);

        public ValueTask<IReadOnlyList<TriggerOccurrence>> ListByDispositionAsync(OccurrenceRoutingDisposition disposition, int limit, CancellationToken cancellationToken = default) =>
            inner.ListByDispositionAsync(disposition, limit, cancellationToken);
    }

    private sealed class SqliteFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
