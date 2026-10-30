using AgentCore.Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed class WorkerReadinessTests
{
    [Fact]
    public async Task Failed_run_pass_is_degraded_then_recovers_without_hiding_other_workers()
    {
        var time = new FakeTimeProvider();
        var readiness = new SchedulerReadiness(time);
        var fail = true;
        using var service = new AgentRunHostedService(_ => fail ? throw new IOException("private-fixture") : Task.CompletedTask,
            time, NullLogger<AgentRunHostedService>.Instance, new SystemDiagnosticIdSource(), readiness: readiness);
        await service.RunPassAsync(default);
        Assert.Equal("degraded", readiness.WorkersSnapshot()["runs"].Status);
        Assert.Equal("starting", readiness.WorkersSnapshot()["workflows"].Status);
        fail = false;
        await service.RunPassAsync(default);
        Assert.Equal("ready", readiness.WorkersSnapshot()["runs"].Status);
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal("stale", readiness.WorkersSnapshot()["runs"].Status);
        Assert.Equal("starting", readiness.Snapshot().Status);
    }
}
