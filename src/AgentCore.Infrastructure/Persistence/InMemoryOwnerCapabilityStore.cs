using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryOwnerCapabilityStore : IOwnerCapabilityStore
{
    private readonly HashSet<string> _hashes = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public ValueTask SaveHashAsync(string tokenHash, DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _hashes.Add(tokenHash);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> ContainsHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_hashes.Contains(tokenHash));
        }
    }
}
