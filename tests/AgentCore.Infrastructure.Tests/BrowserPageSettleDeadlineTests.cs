using System.Reflection;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Infrastructure.Tests;

public sealed class BrowserPageSettleDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_reads_after_a_failed_poll_retain_the_observation_gap(bool malformed)
    {
        var page = DispatchProxy.Create<IPage, RecoveringPage>();
        var recovering = (RecoveringPage)page;
        recovering.Malformed = malformed;
        var result = await BrowserPageSettle.DiagnoseAsync(page, 2500, CancellationToken.None);
        Assert.True(result.Settled);
        Assert.False(result.ObservationAvailable);
        Assert.True(recovering.Reads > 1);
        Assert.Equal(0, result.LastInflight);
    }

    [Fact]
    public async Task Stalled_read_returns_false_at_the_settle_deadline()
    {
        var page = DispatchProxy.Create<IPage, StalledPage>();
        var stalled = (StalledPage)page;
        var wait = BrowserPageSettle.WaitAsync(page, 100, CancellationToken.None);
        await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        try
        {
            Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(stalled.Read.Task.IsCompleted);
        }
        finally
        {
            stalled.Read.TrySetResult([0, 0]);
            await wait.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Caller_cancellation_interrupts_a_stalled_read()
    {
        var page = DispatchProxy.Create<IPage, StalledPage>();
        var stalled = (StalledPage)page;
        using var cancellation = new CancellationTokenSource();
        var wait = BrowserPageSettle.WaitAsync(page, 5_000, cancellation.Token);
        await stalled.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => wait.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(cancellation.Token, error.CancellationToken);
            Assert.False(stalled.Read.Task.IsCompleted);
        }
        finally
        {
            stalled.Read.TrySetResult([0, 0]);
        }
    }

    public class RecoveringPage : DispatchProxy
    {
        public bool Malformed { get; set; }
        public int Reads { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPage.EvaluateAsync)) throw new NotSupportedException(targetMethod?.Name);
            if (++Reads == 1) return Malformed ? Task.FromResult<long[]>([])
                : Task.FromException<long[]>(new PlaywrightException("fixture transient polling failure"));
            return Task.FromResult<long[]>([0, 0]);
        }
    }

    public class StalledPage : DispatchProxy
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<long[]> Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IPage.EvaluateAsync))
                throw new NotSupportedException(targetMethod?.Name);
            Entered.TrySetResult();
            return Read.Task;
        }
    }
}
