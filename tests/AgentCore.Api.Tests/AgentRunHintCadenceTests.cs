using AgentCore.Infrastructure.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Api.Tests;

public sealed class AgentRunHintCadenceTests
{
    [Fact]
    public async Task Slow_Task_hint_pass_does_not_delay_dispatch_cadence_and_shutdown_drains_both_loops()
    {
        var time = new FakeTimeProvider(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispatches = 0; var polls = 0;
        using var service = new AgentRunHostedService(_ => {
            if (Interlocked.Increment(ref dispatches) == 2) second.TrySetResult(); return Task.CompletedTask;
        }, time, NullLogger<AgentRunHostedService>.Instance, new SystemDiagnosticIdSource(), async ct => {
            Interlocked.Increment(ref polls); entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            finally { cancelled.TrySetResult(); }
        });
        await service.StartAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); time.Advance(AgentRunHostedService.Cadence);
            await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, polls); Assert.False(cancelled.Task.IsCompleted);
        }
        finally { await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.True(cancelled.Task.IsCompleted);
    }
}
