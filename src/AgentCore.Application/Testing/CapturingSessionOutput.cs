using AgentCore.Application.Events;

namespace AgentCore.Application.Testing;

public sealed class CapturingSessionOutput : ISessionOutput
{
    private readonly List<SessionOutput> _items = [];
    private readonly List<Waiter> _waiters = [];

    public IReadOnlyList<SessionOutput> Items
    {
        get
        {
            lock (_items)
            {
                return [.. _items];
            }
        }
    }

    public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default)
    {
        List<Waiter> due;
        lock (_items)
        {
            _items.Add(output);
            due = _waiters.Where(waiter => waiter.Match(output)).ToList();
            foreach (var waiter in due)
            {
                _waiters.Remove(waiter);
            }
        }

        foreach (var waiter in due)
        {
            waiter.Completion.TrySetResult(output);
        }

        return ValueTask.CompletedTask;
    }

    public Task<SessionOutput> WaitForAsync(
        Func<SessionOutput, bool> match,
        CancellationToken cancellationToken = default)
    {
        lock (_items)
        {
            var existing = _items.FirstOrDefault(match);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var waiter = new Waiter(match, new TaskCompletionSource<SessionOutput>(TaskCreationOptions.RunContinuationsAsynchronously));
            _waiters.Add(waiter);
            cancellationToken.Register(() => waiter.Completion.TrySetCanceled(cancellationToken));
            return waiter.Completion.Task;
        }
    }

    public IReadOnlyList<TextDeltaOutput> TextDeltas =>
        Items.Select(item => item.Payload).OfType<TextDeltaOutput>().ToArray();

    public IReadOnlyList<ResponseCompletedOutput> Terminals =>
        Items.Select(item => item.Payload).OfType<ResponseCompletedOutput>().ToArray();

    private sealed record Waiter(Func<SessionOutput, bool> Match, TaskCompletionSource<SessionOutput> Completion);
}

public sealed class NoOpSessionOutput : ISessionOutput
{
    public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
