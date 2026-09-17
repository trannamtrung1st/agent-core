using AgentCore.Application.Observability;

namespace AgentCore.Application.Tests;

[Collection("isolated-demo")]
public sealed class RuntimeTelemetrySurfaceTests
{
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
