using System.Text.Json;
using AgentCore.Infrastructure.Events;

namespace AgentCore.Infrastructure.Tests;

[Collection("Event filter memory")]
public sealed class RestrictedEventFilterTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private readonly RestrictedEventFilter filters = new();
    private static JsonElement Envelope => JsonSerializer.SerializeToElement(new { data = new { total = 125, status = "paid", activationKind = "UserTurn" } });
    [Theory]
    [InlineData(null, true)]
    [InlineData("event.data.total >= 100 && event.data.status === 'paid'", true)]
    [InlineData("event.data['status'] === 'paid'", true)]
    [InlineData("event.data.total < 100", false)]
    [InlineData("event.data?.missing === null", false)]
    [InlineData("(event.data.missing ?? false) === false", true)]
    public void Allowed_boolean_expressions_match(string? expression, bool expected)
    { var r = filters.Evaluate(expression, Envelope); Assert.Null(r.Code); Assert.Equal(expected, r.Matched); }
    [Theory]
    [InlineData("event.data.constructor")]
    [InlineData("event['__proto__']")]
    [InlineData("event.data['protot\\u0079pe']")]
    [InlineData("event.data.status = 'paid'")]
    [InlineData("event.data.total + 1 > 2")]
    [InlineData("event.data.total == 125")]
    [InlineData("event.data.total === 0.123456789123456789123")]
    [InlineData("event.data[1]")]
    [InlineData("event.data[event.type]")]
    [InlineData("event.data.status.toString() === 'paid'")]
    [InlineData("globalThis")]
    [InlineData("(() => true)()")]
    [InlineData("true; false")]
    [InlineData("new Date()")]
    [InlineData("/a/.test(event.type)")]
    [InlineData("event.__proto__.constructor('return process')()")]
    public void Untrusted_syntax_never_reaches_engine(string expression)
    { Assert.NotNull(filters.Validate(expression)); Assert.Null(filters.Evaluate(expression, Envelope).Matched); }
    [Fact] public void Ambiguous_json_and_high_precision_decimals_fail_closed()
    {
        using var duplicate = JsonDocument.Parse("{\"data\":{\"total\":100,\"total\":200}}");
        Assert.Equal("filter-envelope-ambiguous", filters.Evaluate("true", duplicate.RootElement).Code);
        using var precision = JsonDocument.Parse("{\"data\":{\"total\":0.123456789123456789123}} ");
        Assert.Equal("filter-unsafe-number", filters.Evaluate("true", precision.RootElement).Code);
        Assert.NotNull(filters.Validate("true ?? false || true"));
    }

    [Fact] public void Nonboolean_errors_and_unsafe_numbers_fail_closed()
    {
        Assert.Equal("filter-result-not-boolean", filters.Evaluate("event.data.total", Envelope).Code);
        Assert.Equal("filter-unsafe-number", filters.Evaluate("event.data.total > 1", JsonSerializer.SerializeToElement(new { data = new { total = 9007199254740993L } })).Code);
        Assert.Equal("filter-source-budget", filters.Validate(new string('x', 1025)));
        Assert.Equal("filter-ast-budget", filters.Validate(new string('(', 20) + "true" + new string(')', 20)));
    }

    [Fact]
    public async Task Concurrent_cold_evaluations_match_or_request_recovery_and_report_timings()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new RestrictedEventFilter().Evaluate("event.data['status'] === 'paid'", Envelope);
            return (Result: result, Milliseconds: watch.Elapsed.TotalMilliseconds);
        })).ToArray();
        start.SetResult();
        var samples = await Task.WhenAll(tasks);
        Assert.All(samples, sample => Assert.True(sample.Result.Matched == true || sample.Result.Retryable, sample.Result.Code));
        // After contention clears, every source still receives its Boolean decision.
        foreach (var sample in samples.Where(s => s.Result.Retryable))
        {
            var retry = filters.Evaluate("event.data['status'] === 'paid'", Envelope);
            Assert.True(retry.Matched, retry.Code);
        }
        var durations = samples.Select(s => s.Milliseconds).Order().ToArray();
        output.WriteLine($"64 concurrent evaluations: matched={samples.Count(s => s.Result.Matched == true)}, retryable={samples.Count(s => s.Result.Retryable)}, p50={durations[32]:F2}ms, p95={durations[60]:F2}ms, max={durations[63]:F2}ms; recovery matched all.");
    }

    [Fact]
    public void Caller_cancellation_propagates_instead_of_becoming_a_filter_error()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => filters.Evaluate("true", Envelope, cancelled.Token));
    }

    [Fact]
    public void Evaluation_exception_releases_worker_and_preserves_fresh_event_state()
    {
        // Missing intermediate property throws inside Jint rather than grammar validation.
        for (var i = 0; i < 8; i++)
            Assert.Equal("filter-evaluation-error", filters.Evaluate("event.missing.value === true", Envelope).Code);
        Assert.True(filters.Evaluate("event.data.status === 'paid'", Envelope).Matched);
        Assert.False(filters.Evaluate("event.data.status === 'paid'",
            JsonSerializer.SerializeToElement(new { data = new { status = "unpaid" } })).Matched);
    }

    [Fact]
    public void Repeated_evaluations_have_bounded_allocations_and_post_gc_heap_growth()
    {
        var envelope = Envelope;
        const string expression = "event.data.total >= 100 && event.data.status === 'paid'";
        // Warm JIT/parser/engine caches before comparing retained managed bytes.
        for (var i = 0; i < 256; i++) Assert.True(filters.Evaluate(expression, envelope).Matched);
        var baseline = CollectHeap();
        for (var batch = 0; batch < 3; batch++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) Assert.True(filters.Evaluate(expression, envelope).Matched);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            var retained = CollectHeap() - baseline;
            output.WriteLine($"Batch {batch + 1}: {allocated / 1000} allocated bytes/evaluation; {retained} retained bytes above warmed baseline.");
            // Deliberately broad regression ceilings, not a Jint/process memory guarantee.
            Assert.InRange(allocated / 1000, 1L, 1024 * 1024L);
            Assert.True(retained < 8 * 1024 * 1024L, $"Post-GC heap grew by {retained} bytes.");
        }
        GC.KeepAlive(envelope);
        GC.KeepAlive(filters);
    }

    private static long CollectHeap()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }
}

// Heap measurements must not overlap other test collections' allocations or evaluations.
[CollectionDefinition("Event filter memory", DisableParallelization = true)]
public sealed class EventFilterMemoryCollection;
