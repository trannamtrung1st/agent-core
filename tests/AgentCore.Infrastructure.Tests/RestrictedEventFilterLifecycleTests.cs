using System.Reflection;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Infrastructure.Events;
using AgentCore.Application.Observability;
using Jint;
using Jint.Native.Object;
using Jint.Runtime;

namespace AgentCore.Infrastructure.Tests;

[Collection("Event filter memory")]
public sealed class RestrictedEventFilterLifecycleTests
{
    [Fact]
    public void Sustained_resource_metrics_count_admitted_engines_without_payload_tags()
    {
        var allocations = new List<long>();
        var durations = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == RuntimeTelemetry.Name && instrument.Name is
                "automation_event_filter_allocated_bytes" or "automation_event_filter_duration_ms")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        { Assert.True(tags.IsEmpty); allocations.Add(value); });
        listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        { Assert.True(tags.IsEmpty); durations.Add(value); });
        listener.Start();
        var filter = new RestrictedEventFilter();
        var envelope = JsonSerializer.SerializeToElement(new { data = new { status = "PRIVATE-SENTINEL" } });
        Assert.True(filter.Evaluate(null, envelope).Matched);
        Assert.NotNull(filter.Evaluate("globalThis", envelope).Code);
        Assert.Empty(allocations);
        for (var i = 0; i < 1024; i++)
            Assert.True(filter.Evaluate("event.data.status === 'PRIVATE-SENTINEL'", envelope).Matched);
        for (var i = 0; i < 8; i++)
            Assert.Equal("filter-evaluation-error", filter.Evaluate("event.missing.value === true", envelope).Code);
        Assert.Equal(1032, allocations.Count);
        Assert.Equal(1032, durations.Count);
        Assert.All(allocations, value => Assert.InRange(value, 1L, 1024 * 1024L));
        Assert.All(durations, value => Assert.True(value >= 0 && double.IsFinite(value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Real_jint_timeout_and_caller_cancellation_dispose_engine_and_release_workers(bool callerCancellation)
    {
        using var caller = new CancellationTokenSource();
        var wrapped = new object();
        var caches = new List<ConditionalWeakTable<object, ObjectInstance>>();
        var envelope = JsonSerializer.SerializeToElement(new { data = new { status = "paid" } });
        var evaluations = 0;
        var filter = new RestrictedEventFilter(options =>
        {
            Engine? engine = null;
            var fired = false;
            options.Interop.TrackObjectWrapperIdentity = true;
            options.UseHostFactory(created => { engine = created; return new Host(); });
            // This last constraint runs after Jint resets its real time/cancellation constraints.
            options.Constraints.Constraints.Add(new ResetAction(() =>
            {
                // Jint also resets constraints on exit; trigger once per engine.
                if (fired) return;
                fired = true;
                var current = Assert.IsType<Engine>(engine);
                // Exercise the exact cache that Jint 4.4.1 Dispose clears. Test-only CLR binding.
                current.SetValue("lifecycleProbe", wrapped);
                var cache = Assert.IsType<ConditionalWeakTable<object, ObjectInstance>>(
                    typeof(Engine).GetField("_objectWrapperCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(current));
                Assert.True(cache.TryGetValue(wrapped, out _));
                caches.Add(cache);
                evaluations++;
                if (callerCancellation) caller.Cancel();
                else
                {
                    var time = options.Constraints.Constraints.Single(c => c.GetType().Name == "TimeConstraint");
                    var timer = Assert.IsType<CancellationTokenSource>(time.GetType()
                        .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(time));
                    // Expire synchronously: the actual Jint TimeConstraint.Check throws TimeoutException.
                    timer.Cancel();
                    Assert.Throws<TimeoutException>(() => time.Check());
                }
            }));
        });

        if (callerCancellation)
            Assert.Throws<OperationCanceledException>(() => filter.Evaluate("event.data.status === 'paid'", envelope, caller.Token));
        else
            // More than four failures detects a leaked slot in the four-worker semaphore.
            for (var i = 0; i < 8; i++)
                Assert.Equal("filter-timeout", filter.Evaluate("event.data.status === 'paid'", envelope).Code);

        Assert.Equal(callerCancellation ? 1 : 8, evaluations);
        Assert.All(caches, cache => Assert.False(cache.TryGetValue(wrapped, out _)));
        Assert.True(new RestrictedEventFilter().Evaluate("event.data.status === 'paid'", envelope).Matched);
    }

    private sealed class ResetAction(Action reset) : Constraint
    {
        public override void Check() { }
        public override void Reset() => reset();
    }
}
