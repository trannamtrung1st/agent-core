using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("telemetry-global")]
public sealed class RuntimeTelemetrySurfaceTests
{
    [Fact]
    public void Stage_statistics_use_the_recent_bounded_window_for_both_record_paths()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        for (var value = 0; value < 2048; value++)
        {
            if (value % 2 == 0) RuntimeTelemetry.Record("window", value);
            else RuntimeTelemetry.RecordDiagnostic("window", value, null);
        }
        var stats = RuntimeTelemetry.SnapshotStats()["window"];
        Assert.Equal(1024, stats.Count);
        Assert.Equal(1535, stats.P50Ms);
        Assert.Equal(1996, stats.P95Ms);
        Assert.Equal(2047, stats.MaxMs);
        Assert.True(RuntimeTelemetry.SnapshotTimeline().Count <= 64);
        RuntimeTelemetry.Reset();
    }

    [Fact]
    public void Concurrent_recording_retains_bounded_consistent_statistics()
    {
        RuntimeTelemetry.Reset();
        Parallel.For(0, 4096, index => RuntimeTelemetry.Record("concurrent", index));
        var stats = RuntimeTelemetry.SnapshotStats()["concurrent"];
        Assert.Equal(1024, stats.Count);
        Assert.InRange(stats.P50Ms, 0, 4095);
        Assert.InRange(stats.P95Ms, stats.P50Ms, 4095);
        Assert.InRange(stats.MaxMs, stats.P95Ms, 4095);
        RuntimeTelemetry.Reset();
    }

    [Fact]
    public void Default_timeline_omits_detail_and_records_post_mvp_stages()
    {
        RuntimeTelemetry.Reset();
        RuntimeTelemetry.Configure(64, contentLogging: false);
        RuntimeTelemetry.Record("extraction", 1);
        RuntimeTelemetry.Record("workspace", 2);
        RuntimeTelemetry.Record("tools", 3, "sandbox.run");
        RuntimeTelemetry.Record("sandbox", 4, "echo");
        RuntimeTelemetry.Record("initiative", 5);
        RuntimeTelemetry.Record("cleanup", 6);
        RuntimeTelemetry.Record("tools", 7, "Bearer sk-secret");
        var events = RuntimeTelemetry.SnapshotTimeline();
        Assert.All(events, item => Assert.Null(item.Detail));
        var stats = RuntimeTelemetry.SnapshotStats();
        Assert.Contains("extraction", stats.Keys);
        Assert.Contains("workspace", stats.Keys);
        Assert.Contains("tools", stats.Keys);
        Assert.Contains("sandbox", stats.Keys);
        Assert.Contains("initiative", stats.Keys);
        Assert.Contains("cleanup", stats.Keys);
    }
}
