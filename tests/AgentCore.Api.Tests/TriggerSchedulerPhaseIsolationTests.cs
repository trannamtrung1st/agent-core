using AgentCore.Application.Ports;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Api.Tests;

public sealed class TriggerSchedulerPhaseIsolationTests
{
    private sealed class Diagnostics : IDiagnosticIdSource { public Guid NewId() => Guid.NewGuid(); }

    [Fact]
    public async Task A_failed_event_phase_does_not_starve_webhook_schedule_or_routing()
    {
        var observed = new List<string>();
        await TriggerSchedulerHostedService.RunPhasesAsync([
            _ => throw new InvalidOperationException("Unavailable Core event store"),
            _ => { observed.Add("webhook"); return Task.CompletedTask; },
            _ => { observed.Add("schedule"); return Task.CompletedTask; },
            _ => { observed.Add("routing"); return Task.CompletedTask; }
        ], NullLogger<TriggerSchedulerHostedService>.Instance, new Diagnostics(), default);
        Assert.Equal(new[] { "webhook", "schedule", "routing" }, observed);
    }

    [Fact]
    public async Task Shutdown_cancellation_stops_remaining_phases()
    {
        using var source = new CancellationTokenSource();
        var next = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TriggerSchedulerHostedService.RunPhasesAsync([
            ct => { source.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            _ => { next = true; return Task.CompletedTask; }
        ], NullLogger<TriggerSchedulerHostedService>.Instance, new Diagnostics(), source.Token));
        Assert.False(next);
    }
}
