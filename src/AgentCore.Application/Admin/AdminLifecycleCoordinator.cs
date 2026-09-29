using System.Collections.Concurrent;

namespace AgentCore.Application.Admin;

public sealed class AdminLifecycleCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private int _waiters;

    internal int Waiters => Volatile.Read(ref _waiters);

    public ValueTask WithInstanceAsync(
        Guid instanceId,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default) =>
        WithAsync(InstanceKey(instanceId), action, cancellationToken);

    public ValueTask<T> WithInstanceAsync<T>(
        Guid instanceId,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default) =>
        WithAsync(InstanceKey(instanceId), action, cancellationToken);

    public ValueTask WithDefinitionAsync(
        string definitionId,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default) =>
        WithAsync(DefinitionKey(definitionId), action, cancellationToken);

    public ValueTask<T> WithDefinitionAsync<T>(
        string definitionId,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default) =>
        WithAsync(DefinitionKey(definitionId), action, cancellationToken);

    internal static string InstanceKey(Guid instanceId) => "instance:" + instanceId.ToString("D");

    internal static string DefinitionKey(string definitionId) => "definition:" + definitionId;

    private async ValueTask WithAsync(
        string key,
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref _waiters);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }

        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async ValueTask<T> WithAsync<T>(
        string key,
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken)
    {
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref _waiters);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiters);
        }

        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
