using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed class SchedulerReadinessTests
{
    [Fact]
    public async Task Pass_failure_recovery_and_staleness_are_truthful()
    {
        var time = new Clock();
        var readiness = new SchedulerReadiness(time);
        Assert.Equal("starting", readiness.Snapshot().Status);
        var succeeds = false;
        var service = new TriggerSchedulerHostedService(_ => succeeds ? Task.CompletedTask : throw new InvalidOperationException("fixture"),
            time, NullLogger<TriggerSchedulerHostedService>.Instance, new Diagnostics(), readiness);
        await service.RunPassAsync(default);
        Assert.Equal("degraded", readiness.Snapshot().Status);
        Assert.Null(readiness.Snapshot().LastSuccessAt);
        succeeds = true;
        await service.RunPassAsync(default);
        Assert.Equal("ready", readiness.Snapshot().Status);
        Assert.Equal(readiness.Snapshot().LastPassAt, readiness.Snapshot().LastSuccessAt);
        time.Now = time.Now.AddSeconds(11);
        Assert.Equal("stale", readiness.Snapshot().Status);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }
}
