using System.Text.Json;
using AgentCore.Infrastructure.Events;

namespace AgentCore.Infrastructure.Tests;

[Collection("Event filter memory")]
public sealed class RestrictedEventFilterMemoryTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("event.data.total >= 100 && event.data.status === 'paid'", true, null)]
    [InlineData("event.data.total < 100", false, null)]
    [InlineData("event.data.total", null, "filter-result-not-boolean")]
    [InlineData("event.missing.value === true", null, "filter-evaluation-error")]
    public void Repeated_evaluations_have_bounded_allocations_and_post_gc_heap_growth(
        string expression, bool? matched, string? code)
    {
        var filters = new RestrictedEventFilter();
        var envelope = JsonSerializer.SerializeToElement(new { data = new { total = 125, status = "paid" } });
        void Evaluate()
        {
            var result = filters.Evaluate(expression, envelope);
            Assert.Equal(matched, result.Matched);
            Assert.Equal(code, result.Code);
        }
        // Warm the chosen success/error path before comparing retained managed bytes.
        for (var i = 0; i < 256; i++) Evaluate();
        var baseline = CollectHeap();
        for (var batch = 0; batch < 3; batch++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++) Evaluate();
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
