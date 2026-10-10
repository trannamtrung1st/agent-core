using System.Text.Json;
using AgentCore.Infrastructure.Events;

namespace AgentCore.Infrastructure.Tests;

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
}
