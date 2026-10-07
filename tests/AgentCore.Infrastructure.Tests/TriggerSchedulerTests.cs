using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class TriggerSchedulerTests
{
    private static readonly Guid InstanceA = Guid.Parse("019944af-0009-7000-8000-0000000000a1");
    private static readonly Guid ProfileA = Guid.Parse("019944af-0009-7000-8000-0000000000b1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-0009-7000-8000-0000000000a2");
    private static readonly Guid ProfileB = Guid.Parse("019944af-0009-7000-8000-0000000000b2");
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Due = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Expired_claims_return_to_pending_and_a_second_claim_loses()
    {
        await ForEachStore(async store =>
        {
            var now = Due;
            var occurrence = new TriggerOccurrence(
                Guid.NewGuid(),
                $"schedule:{Guid.NewGuid():D}",
                null,
                new TriggerOwner(InstanceA, ProfileA),
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
            Assert.NotNull(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, claim, now.AddSeconds(30), now));
            Assert.Null(await store.TryClaimOccurrenceAsync(occurrence.OccurrenceId, Guid.NewGuid(), now.AddSeconds(30), now));
            Assert.Equal(1, await store.RecoverExpiredClaimsAsync(now.AddSeconds(30)));
            var pending = await store.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
            Assert.Equal(occurrence.OccurrenceId, Assert.Single(pending).OccurrenceId);
        });
    }

    [Fact]
    public async Task Due_boundaries_one_shot_recovery_and_recurrence_are_idempotent()
    {
        await ForEachStore(async store =>
        {
            var logs = new CaptureLogger();
            var scheduler = new TriggerScheduler(store, logs);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var daily = await CreateAsync(store, owner, new DailySchedule(1, new TimeOnly(9, 0), "UTC"), Due, "secret-intent-sentinel");
            var before = await scheduler.RunOnceAsync(Due.AddMilliseconds(-1));
            Assert.Equal(0, before.Scanned);
            Assert.Equal(1, (await store.GetAsync(owner, daily.AutomationId))!.Revision);

            using var listener = new MeterListener();
            var admitted = 0;
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name == "trigger_scheduler_events")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var matchedAdmitted = false;
                foreach (var tag in tags)
                {
                    Assert.NotEqual("instructions", tag.Key);
                    matchedAdmitted |= string.Equals(
                        tag.Value?.ToString(),
                        ScheduledAdmitOutcome.Admitted.ToString(),
                        StringComparison.Ordinal);
                }

                if (matchedAdmitted)
                {
                    admitted++;
                }
            });
            listener.Start();

            var exact = await scheduler.RunOnceAsync(Due);
            Assert.Equal(1, exact.Admitted);
            var advanced = await store.GetAsync(owner, daily.AutomationId);
            Assert.Equal(2, advanced!.Revision);
            Assert.Equal(1, advanced.ScheduleRevision);
            Assert.Equal(1, advanced.OccurrenceCount);
            Assert.Equal(Due.AddDays(1), advanced.NextOccurrenceAtUtc);
            Assert.Equal(AutomationStatus.Active, advanced.Status);
            var occurrence = await store.GetOccurrenceAsync(owner, exact.OccurrenceIds[0]);
            Assert.Equal(Due, occurrence!.ScheduledAtUtc);
            Assert.Equal(OccurrenceRoutingDisposition.Pending, occurrence.Disposition);
            Assert.Contains("secret-intent-sentinel", occurrence.EvidenceJson, StringComparison.Ordinal);

            var again = await scheduler.RunOnceAsync(Due);
            Assert.Equal(0, again.Admitted);
            Assert.Equal(1, (await store.GetAsync(owner, daily.AutomationId))!.OccurrenceCount);
            Assert.True(admitted >= 1);
            Assert.DoesNotContain(logs.Messages, message => message.Contains("secret-intent-sentinel", StringComparison.Ordinal));
            await new AutomationService(store, new SystemIdGenerator(TimeProvider.System), Clock())
                .CancelAsync(owner, daily.AutomationId, advanced.Revision);

            var shot = await CreateAsync(
                store,
                owner,
                new OneShotSchedule(Due, "UTC"),
                Due,
                "Call once");
            var recovered = await scheduler.RunOnceAsync(Due.AddDays(2));
            Assert.Equal(1, recovered.Admitted);
            var completed = await store.GetAsync(owner, shot.AutomationId);
            Assert.Equal(AutomationStatus.Completed, completed!.Status);
            Assert.Null(completed.NextOccurrenceAtUtc);
            Assert.Equal(1, completed.ScheduleRevision);
            var second = await scheduler.RunOnceAsync(Due.AddDays(3));
            Assert.Equal(0, second.Admitted);
            Assert.Equal(1, (await store.GetAsync(owner, shot.AutomationId))!.OccurrenceCount);
        });
    }

    [Fact]
    public async Task Missed_daily_slots_coalesce_and_weekly_wall_clock_advances()
    {
        await ForEachStore(async store =>
        {
            var scheduler = Scheduler(store);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var daily = await CreateAsync(store, owner, new DailySchedule(1, new TimeOnly(9, 0), "UTC"), Due, "Call John");
            var coalesced = await scheduler.RunOnceAsync(new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero));
            Assert.Equal(1, coalesced.Admitted);
            var loaded = await store.GetAsync(owner, daily.AutomationId);
            Assert.Equal(1, loaded!.OccurrenceCount);
            Assert.Equal(new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero), loaded.NextOccurrenceAtUtc);
            var evidence = (await store.GetOccurrenceAsync(owner, coalesced.OccurrenceIds[0]))!.EvidenceJson;
            Assert.Contains("\"skippedCount\":4", evidence, StringComparison.Ordinal);
            await new AutomationService(store, new SystemIdGenerator(TimeProvider.System), Clock())
                .CancelAsync(owner, daily.AutomationId, loaded.Revision);

            var monday = new DateTimeOffset(2026, 9, 7, 9, 0, 0, TimeSpan.Zero);
            Assert.Equal(DayOfWeek.Monday, monday.DayOfWeek);
            var weekly = await CreateAsync(
                store,
                owner,
                new WeeklySchedule(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "UTC"),
                monday,
                "Every Monday");
            var progressed = await scheduler.RunOnceAsync(monday);
            Assert.Equal(1, progressed.Admitted);
            var nextWeek = await store.GetAsync(owner, weekly.AutomationId);
            Assert.Equal(monday.AddDays(7), nextWeek!.NextOccurrenceAtUtc);
            Assert.Equal(1, nextWeek.ScheduleRevision);
        });
    }

    [Fact]
    public async Task London_weekly_schedule_does_not_become_a_fixed_utc_interval()
    {
        await ForEachStore(async store =>
        {
            var (before, after) = MondayPair("Europe/London");
            var first = TriggerScheduleCalculator.ResolveWallClock("Europe/London", before, new TimeOnly(9, 0));
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await CreateAsync(
                store,
                owner,
                new WeeklySchedule(1, [DayOfWeek.Monday], new TimeOnly(9, 0), "Europe/London"),
                first,
                "London Monday",
                createdAt: first.AddMinutes(-30));
            var pass = await Scheduler(store).RunOnceAsync(first);
            Assert.Equal(1, pass.Admitted);
            var loaded = await store.GetAsync(owner, created.AutomationId);
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var local = TimeZoneInfo.ConvertTime(loaded!.NextOccurrenceAtUtc!.Value, zone);
            Assert.Equal(DayOfWeek.Monday, local.DayOfWeek);
            Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(local.DateTime));
            Assert.Equal(after, DateOnly.FromDateTime(local.DateTime));
            Assert.NotEqual(TimeSpan.FromDays(7), loaded.NextOccurrenceAtUtc.Value - first);
        });
    }

    [Fact]
    public async Task Expiry_cap_cancel_and_reschedule_do_not_admit_a_stale_slot()
    {
        await ForEachStore(async store =>
        {
            var scheduler = Scheduler(store);
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var expired = await CreateAsync(
                store,
                owner,
                new OneShotSchedule(Due, "UTC"),
                Due,
                "Expired shot",
                Due.AddHours(1));
            var expiry = await scheduler.RunOnceAsync(Due.AddDays(1));
            Assert.Equal(1, expiry.Expired);
            Assert.Equal(AutomationStatus.Expired, (await store.GetAsync(owner, expired.AutomationId))!.Status);
            Assert.Empty(expiry.OccurrenceIds);

            var capped = await CreateAsync(
                store,
                owner,
                new DailySchedule(1, new TimeOnly(9, 0), "UTC", maxOccurrences: 1),
                Due.AddDays(10),
                "Once");
            var once = await scheduler.RunOnceAsync(Due.AddDays(10));
            Assert.Equal(1, once.Admitted);
            Assert.Equal(AutomationStatus.Completed, (await store.GetAsync(owner, capped.AutomationId))!.Status);

            var cancelled = await CreateAsync(store, owner, new DailySchedule(1, new TimeOnly(9, 0), "UTC"), Due.AddDays(20), "Cancel me");
            await new AutomationService(store, new SystemIdGenerator(TimeProvider.System), Clock())
                .CancelAsync(owner, cancelled.AutomationId, cancelled.Revision);
            var cancelPass = await scheduler.RunOnceAsync(Due.AddDays(20));
            Assert.Equal(0, cancelPass.Admitted);
            var staleCancel = await store.TryAdmitScheduledAsync(
                owner,
                cancelled.AutomationId,
                cancelled.ScheduleRevision,
                Due.AddDays(20),
                Due.AddDays(20));
            Assert.Equal(ScheduledAdmitOutcome.Stale, staleCancel.Outcome);
            Assert.Null(await store.GetOccurrenceAsync(owner, TriggerScheduleAdmission.OccurrenceId(
                TriggerScheduleAdmission.DedupeKey(cancelled.AutomationId, cancelled.ScheduleRevision, Due.AddDays(20)))));

            var moving = await CreateAsync(store, owner, new DailySchedule(1, new TimeOnly(9, 0), "UTC"), Due.AddDays(30), "Move me");
            var later = Due.AddDays(40);
            await new AutomationService(store, new SystemIdGenerator(TimeProvider.System), Clock()).UpdateAsync(
                owner,
                moving.AutomationId,
                moving.Revision,
                AutomationChange.ScheduleOnly(new DailySchedule(1, new TimeOnly(11, 0), "UTC"), later, null));
            var stale = await store.TryAdmitScheduledAsync(
                owner,
                moving.AutomationId,
                moving.ScheduleRevision,
                Due.AddDays(30),
                Due.AddDays(30));
            Assert.Equal(ScheduledAdmitOutcome.Stale, stale.Outcome);
            Assert.Equal(later, (await store.GetAsync(owner, moving.AutomationId))!.NextOccurrenceAtUtc);
        });
    }

    [Fact]
    public async Task Owners_stay_isolated_and_a_transient_failure_retries()
    {
        await ForEachStore(async store =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var other = new TriggerOwner(InstanceB, ProfileB);
            var first = await CreateAsync(store, owner, new OneShotSchedule(Due, "UTC"), Due, "Owner A");
            var second = await CreateAsync(store, other, new OneShotSchedule(Due, "UTC"), Due, "Owner B");
            var pass = await Scheduler(store).RunOnceAsync(Due.AddMinutes(5));
            Assert.Equal(2, pass.Admitted);
            Assert.NotNull(await store.GetOccurrenceAsync(owner, OccurrenceFor(pass, first)));
            Assert.Null(await store.GetOccurrenceAsync(other, OccurrenceFor(pass, first)));
            Assert.NotNull(await store.GetOccurrenceAsync(other, OccurrenceFor(pass, second)));

            var flakyRegistration = await CreateAsync(
                store,
                owner,
                new OneShotSchedule(Due.AddDays(3), "UTC"),
                Due.AddDays(3),
                "secret-intent-sentinel");
            var logs = new CaptureLogger();
            var flaky = new FlakyStore(store);
            var flakyPass = await new TriggerScheduler(flaky, logs).RunOnceAsync(Due.AddDays(3));
            Assert.Equal(1, flakyPass.Failed);
            Assert.Equal(1, (await store.GetAsync(owner, flakyRegistration.AutomationId))!.Revision);
            Assert.Equal(AutomationStatus.Active, (await store.GetAsync(owner, flakyRegistration.AutomationId))!.Status);
            var retried = await new TriggerScheduler(flaky, logs).RunOnceAsync(Due.AddDays(3));
            Assert.Equal(1, retried.Admitted);
            Assert.Equal(AutomationStatus.Completed, (await store.GetAsync(owner, flakyRegistration.AutomationId))!.Status);
            Assert.DoesNotContain(logs.Messages, message => message.Contains("secret-intent-sentinel", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task Registration_failure_logs_one_diagnostic_id_and_the_exception()
    {
        await ForEachStore(async store =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var registration = await CreateAsync(
                store,
                owner,
                new OneShotSchedule(Due.AddDays(3), "UTC"),
                Due.AddDays(3),
                "secret-intent-sentinel");
            var diagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
            var businessId = Guid.Parse("019944af-00d7-7000-8000-0000000000b1");
            var sessionId = Guid.Parse("019944af-00d7-7000-8000-0000000000a1");
            var business = new DeterministicIdGenerator([businessId], [sessionId]);
            var logs = new DiagnosticLogCapture<TriggerScheduler>();
            using var metrics = RuntimeMetricProbe.Start();
            using var listener = DiagnosticActivity.Listen();
            using var activity = RuntimeTelemetry.Activity.StartActivity("scheduler-registration-failure");
            Assert.NotNull(activity);

            var pass = await new TriggerScheduler(new FlakyStore(store), logs, new QueueDiagnosticIdSource([diagnosticId]))
                .RunOnceAsync(Due.AddDays(3));

            Assert.Equal(1, pass.Failed);
            var entry = Assert.Single(logs.Entries, candidate => candidate.Level == LogLevel.Warning);
            Assert.Equal(LogLevel.Warning, entry.Level);
            var failure = Assert.IsType<IOException>(entry.Exception);
            Assert.Equal("transient", failure.Message);
            Assert.Same(failure, entry.Exception);
            Assert.Equal(diagnosticId, entry.Properties["DiagnosticId"]);
            Assert.Equal(registration.AutomationId, entry.Properties["AutomationId"]);
            Assert.Equal(activity.TraceId.ToHexString(), entry.Properties["TraceId"]);
            Assert.Contains(diagnosticId.ToString(), entry.Message, StringComparison.Ordinal);
            Assert.Contains(registration.AutomationId.ToString(), entry.Message, StringComparison.Ordinal);
            Assert.Contains(activity.TraceId.ToHexString(), entry.Message, StringComparison.Ordinal);
            Assert.Contains("Trigger scan failed for registration.", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-intent-sentinel", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("transient", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("IOException", entry.Message, StringComparison.Ordinal);
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal(businessId, business.NewId());
            AssertBoundedMetrics(metrics, diagnosticId);
        });
    }

    [Fact]
    public async Task Registration_failure_simple_console_prints_ids_and_the_exception()
    {
        var store = new InMemoryTriggerStore();
        var owner = new TriggerOwner(InstanceA, ProfileA);
        var registration = await CreateAsync(
            store,
            owner,
            new OneShotSchedule(Due.AddDays(6), "UTC"),
            Due.AddDays(6),
            "secret-intent-sentinel");
        var diagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
        using var listener = DiagnosticActivity.Listen();
        using var activity = RuntimeTelemetry.Activity.StartActivity("registration-console");
        Assert.NotNull(activity);
        var traceId = activity.TraceId.ToHexString();
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            using var factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Warning);
                builder.AddSimpleConsole();
            });
            var logger = factory.CreateLogger<TriggerScheduler>();
            var pass = await new TriggerScheduler(
                    new FlakyStore(store),
                    logger,
                    new QueueDiagnosticIdSource([diagnosticId]))
                .RunOnceAsync(Due.AddDays(6));
            Assert.Equal(1, pass.Failed);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var printed = stdout.ToString();
        Assert.Contains("Trigger scan failed for registration.", printed, StringComparison.Ordinal);
        Assert.Contains(diagnosticId.ToString(), printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(traceId, printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(registration.AutomationId.ToString(), printed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IOException", printed, StringComparison.Ordinal);
        Assert.Contains("transient", printed, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-intent-sentinel", printed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlapping_scans_and_a_bounded_batch_admit_each_slot_once()
    {
        await ForEachStore(async store =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await CreateAsync(store, owner, new OneShotSchedule(Due.AddDays(4), "UTC"), Due.AddDays(4), "Overlap");
            var scheduler = Scheduler(store);
            var passes = await Task.WhenAll(
                scheduler.RunOnceAsync(Due.AddDays(4)),
                scheduler.RunOnceAsync(Due.AddDays(4)));
            Assert.Equal(1, passes.Sum(pass => pass.Admitted));
            Assert.Equal(0, passes.Sum(pass => pass.Failed));
            var loaded = await store.GetAsync(owner, created.AutomationId);
            Assert.Equal(1, loaded!.OccurrenceCount);
            await scheduler.RunOnceAsync(Due.AddDays(4));
            Assert.Equal(1, (await store.GetAsync(owner, created.AutomationId))!.OccurrenceCount);

            var batchStore = store;
            await CreateAsync(batchStore, owner, new OneShotSchedule(Due.AddDays(5), "UTC"), Due.AddDays(5), "Batch 1");
            await CreateAsync(batchStore, owner, new OneShotSchedule(Due.AddDays(5), "UTC"), Due.AddDays(5), "Batch 2");
            await CreateAsync(batchStore, owner, new OneShotSchedule(Due.AddDays(5), "UTC"), Due.AddDays(5), "Batch 3");
            var limited = new TriggerScheduler(batchStore, new CaptureLogger(), batchSize: 2);
            var first = await limited.RunOnceAsync(Due.AddDays(5));
            Assert.Equal(2, first.Admitted);
            var secondPass = await limited.RunOnceAsync(Due.AddDays(5));
            Assert.Equal(1, secondPass.Admitted);
        });
    }

    [Fact]
    public async Task Unavailable_timezone_suspends_without_an_occurrence()
    {
        await ForEachStore(async store =>
        {
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await CreateAsync(
                store,
                owner,
                new DailySchedule(1, new TimeOnly(9, 0), "Fake/Nowhere"),
                Due,
                "Bad zone");
            var pass = await Scheduler(store).RunOnceAsync(Due);
            Assert.Equal(1, pass.Rejected);
            Assert.Empty(pass.OccurrenceIds);
            var loaded = await store.GetAsync(owner, created.AutomationId);
            Assert.Equal(AutomationStatus.SuspendedPolicy, loaded!.Status);
            Assert.Equal("Timezone is unavailable.", loaded.SuspensionReason);
            Assert.Equal(0, (await Scheduler(store).RunOnceAsync(Due.AddDays(1))).Scanned);
        });
    }

    [Fact]
    public async Task Sqlite_restart_admits_a_missed_one_shot_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-schedule-{Guid.NewGuid():N}.db");
        var factory = new TestFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, new FakeTimeProvider(Created)).EnsureCreatedAsync();
            var owner = new TriggerOwner(InstanceA, ProfileA);
            var created = await CreateAsync(new SqliteTriggerStore(factory), owner, new OneShotSchedule(Due, "UTC"), Due, "After restart");
            var first = await Scheduler(new SqliteTriggerStore(factory)).RunOnceAsync(Due.AddHours(3));
            Assert.Equal(1, first.Admitted);
            var reopened = new SqliteTriggerStore(factory);
            var second = await Scheduler(reopened).RunOnceAsync(Due.AddHours(4));
            Assert.Equal(0, second.Admitted);
            var loaded = await reopened.GetAsync(owner, created.AutomationId);
            Assert.Equal(AutomationStatus.Completed, loaded!.Status);
            Assert.Equal(1, loaded.OccurrenceCount);
            Assert.NotNull(await reopened.GetOccurrenceAsync(owner, first.OccurrenceIds[0]));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private static Guid OccurrenceFor(TriggerSchedulerPass pass, Automation registration)
    {
        var key = TriggerScheduleAdmission.DedupeKey(registration.AutomationId, registration.ScheduleRevision, registration.NextOccurrenceAtUtc!.Value);
        var id = TriggerScheduleAdmission.OccurrenceId(key);
        Assert.Contains(id, pass.OccurrenceIds);
        return id;
    }

    private static (DateOnly Before, DateOnly After) MondayPair(string timeZoneId)
    {
        for (var day = new DateOnly(2026, 1, 5); day < new DateOnly(2026, 12, 1); day = day.AddDays(1))
        {
            if (day.DayOfWeek != DayOfWeek.Monday)
            {
                continue;
            }

            var next = day.AddDays(7);
            var first = TriggerScheduleCalculator.ResolveWallClock(timeZoneId, day, new TimeOnly(9, 0));
            var second = TriggerScheduleCalculator.ResolveWallClock(timeZoneId, next, new TimeOnly(9, 0));
            if (second - first != TimeSpan.FromDays(7))
            {
                return (day, next);
            }
        }

        throw new InvalidOperationException("Expected a DST boundary.");
    }

    private static TriggerScheduler Scheduler(ITriggerStore store) => new(store, new CaptureLogger());

    private static async Task<Automation> CreateAsync(
        ITriggerStore store,
        TriggerOwner owner,
        TriggerSchedule schedule,
        DateTimeOffset next,
        string intent,
        DateTimeOffset? expires = null,
        DateTimeOffset? createdAt = null)
    {
        var created = await new AutomationService(
            store,
            new SystemIdGenerator(TimeProvider.System),
            new FakeTimeProvider(createdAt ?? Created)).CreateAsync(new AutomationDraft(
            owner,
            intent,
            schedule,
            next,
            expires,
            TriggerAuthorizationOrigin.CurrentUserTurn,
            null,
            null));
        Assert.Equal(next, created.NextOccurrenceAtUtc);
        return created;
    }

    private static void AssertBoundedMetrics(RuntimeMetricProbe metrics, Guid diagnosticId)
    {
        var tags = metrics.Tags;
        Assert.Contains(tags, tag => tag.Key == "outcome" && Equals(tag.Value, "Failed"));
        foreach (var tag in tags)
        {
            Assert.DoesNotContain(tag.Key, RuntimeMetricProbe.IdentityTagNames);
            Assert.NotEqual(diagnosticId, tag.Value);
            Assert.DoesNotContain(diagnosticId.ToString(), tag.Value?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static FakeTimeProvider Clock() => new(Created);

    private static async Task ForEachStore(Func<ITriggerStore, Task> exercise)
    {
        await exercise(new InMemoryTriggerStore());
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-schedule-{Guid.NewGuid():N}.db");
        var factory = new TestFactory(new DbContextOptionsBuilder<AgentCoreDbContext>().UseSqlite($"Data Source={path}").Options);
        try
        {
            await new SqliteMemoryStore(factory, Clock()).EnsureCreatedAsync();
            await exercise(new SqliteTriggerStore(factory));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            SqliteConnection.ClearPool(connection);
            File.Delete(path);
        }
    }

    private sealed class FlakyStore(ITriggerStore inner) : ITriggerStore
    {
        private int _failures = 1;

        public ValueTask<Automation> CreateAsync(Automation registration, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(registration, cancellationToken);

        public ValueTask<Automation?> GetAsync(TriggerOwner owner, Guid automationId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(owner, automationId, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListAsync(TriggerOwner owner, AutomationStatus? status, CancellationToken cancellationToken = default) =>
            inner.ListAsync(owner, status, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListSuspendedPolicyForAgentInstanceAsync(
            Guid agentInstanceId,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ListSuspendedPolicyForAgentInstanceAsync(agentInstanceId, limit, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListFutureRegistrationsForAgentInstanceAsync(
            Guid agentInstanceId,
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ListFutureRegistrationsForAgentInstanceAsync(agentInstanceId, limit, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListAutomationsPageAsync(
            TriggerOwner owner, int limit, Guid? before, CancellationToken cancellationToken = default) =>
            inner.ListAutomationsPageAsync(owner, limit, before, cancellationToken);

        public ValueTask<int> CountActiveAsync(TriggerOwner owner, CancellationToken cancellationToken = default) =>
            inner.CountActiveAsync(owner, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListEventSubscriptionsAsync(
            Guid eventSourceId,
            string eventType,
            CancellationToken cancellationToken = default) =>
            inner.ListEventSubscriptionsAsync(eventSourceId, eventType, cancellationToken);

        public ValueTask<Automation> UpdateAsync(TriggerOwner owner, Guid automationId, long expectedRevision, string intent, TriggerSchedule schedule, DateTimeOffset? nextOccurrenceAtUtc, DateTimeOffset? expiresAtUtc, DateTimeOffset updatedAt, CancellationToken cancellationToken = default) =>
            inner.UpdateAsync(owner, automationId, expectedRevision, intent, schedule, nextOccurrenceAtUtc, expiresAtUtc, updatedAt, cancellationToken);

        public ValueTask<Automation> CancelAsync(TriggerOwner owner, Guid automationId, long expectedRevision, DateTimeOffset cancelledAt, CancellationToken cancellationToken = default) =>
            inner.CancelAsync(owner, automationId, expectedRevision, cancelledAt, cancellationToken);

        public ValueTask<TriggerOccurrenceAdmitResult> AdmitOccurrenceAsync(TriggerOccurrence occurrence, CancellationToken cancellationToken = default) =>
            inner.AdmitOccurrenceAsync(occurrence, cancellationToken);

        public ValueTask<TriggerOccurrence?> GetOccurrenceAsync(TriggerOwner owner, Guid occurrenceId, CancellationToken cancellationToken = default) =>
            inner.GetOccurrenceAsync(owner, occurrenceId, cancellationToken);

        public ValueTask<IReadOnlyList<Automation>> ListDueAsync(DateTimeOffset asOfUtc, int limit, CancellationToken cancellationToken = default) =>
            inner.ListDueAsync(asOfUtc, limit, cancellationToken);

        public ValueTask<ScheduledAdmitResult> TryAdmitScheduledAsync(TriggerOwner owner, Guid automationId, long expectedScheduleRevision, DateTimeOffset expectedNextOccurrenceAtUtc, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Decrement(ref _failures) >= 0)
            {
                throw new IOException("transient");
            }

            return inner.TryAdmitScheduledAsync(owner, automationId, expectedScheduleRevision, expectedNextOccurrenceAtUtc, asOfUtc, cancellationToken);
        }

        public ValueTask<Automation?> SuspendPolicyAsync(TriggerOwner owner, Guid automationId, long expectedRevision, string reason, DateTimeOffset suspendedAt, CancellationToken cancellationToken = default) =>
            inner.SuspendPolicyAsync(owner, automationId, expectedRevision, reason, suspendedAt, cancellationToken);

        public ValueTask<Automation?> TryReactivatePolicySuspensionAsync(TriggerOwner owner, Guid automationId, long expectedRevision, DateTimeOffset reactivatedAt, CancellationToken cancellationToken = default) =>
            inner.TryReactivatePolicySuspensionAsync(owner, automationId, expectedRevision, reactivatedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryClaimOccurrenceAsync(Guid occurrenceId, Guid claimId, DateTimeOffset leaseExpiresAtUtc, DateTimeOffset claimedAt, CancellationToken cancellationToken = default) =>
            inner.TryClaimOccurrenceAsync(occurrenceId, claimId, leaseExpiresAtUtc, claimedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> TryAcceptLiveAsync(Guid occurrenceId, Guid claimId, DateTimeOffset acceptedAt, CancellationToken cancellationToken = default) =>
            inner.TryAcceptLiveAsync(occurrenceId, claimId, acceptedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> ConfirmLiveBeginAsync(
            Guid occurrenceId,
            long expectedRoutingRevision,
            DateTimeOffset confirmedAt,
            CancellationToken cancellationToken = default) =>
            inner.ConfirmLiveBeginAsync(occurrenceId, expectedRoutingRevision, confirmedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> RevertLivePreparedAsync(
            Guid occurrenceId,
            long expectedRoutingRevision,
            DateTimeOffset revertedAt,
            CancellationToken cancellationToken = default) =>
            inner.RevertLivePreparedAsync(occurrenceId, expectedRoutingRevision, revertedAt, cancellationToken);

        public ValueTask<TriggerOccurrence?> PromoteLivePreparedAwaitingDurableWorkAsync(
            Guid occurrenceId,
            long expectedRoutingRevision,
            string reason,
            DateTimeOffset markedAt,
            CancellationToken cancellationToken = default) =>
            inner.PromoteLivePreparedAwaitingDurableWorkAsync(
                occurrenceId,
                expectedRoutingRevision,
                reason,
                markedAt,
                cancellationToken);

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

    private sealed class CaptureLogger : ILogger<TriggerScheduler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class TestFactory(DbContextOptions<AgentCoreDbContext> options) : IDbContextFactory<AgentCoreDbContext>
    {
        public AgentCoreDbContext CreateDbContext() => new(options);

        public Task<AgentCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
