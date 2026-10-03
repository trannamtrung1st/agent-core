using System.Collections.Concurrent;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Connections;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryApplicationConnectionStore : IApplicationConnectionStore
{
    private readonly ConcurrentDictionary<Guid, ApplicationConnection> _rows = new();

    public ValueTask<ApplicationConnection?> GetByAgentAsync(
        Guid agentInstanceId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_rows.TryGetValue(agentInstanceId, out var row) ? row : null);
    }

    public ValueTask<ApplicationConnection> SaveAsync(
        ApplicationConnection connection,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(connection);
        while (true)
        {
            if (!_rows.TryGetValue(connection.AgentInstanceId, out var current))
            {
                if (expectedRevision != 0)
                {
                    throw AgentCoreErrors.Conflict("Application connection revision is stale.");
                }

                RejectDuplicateWebhookKey(connection);
                if (_rows.TryAdd(connection.AgentInstanceId, connection))
                {
                    return ValueTask.FromResult(connection);
                }

                continue;
            }

            if (current.Revision != expectedRevision)
            {
                throw AgentCoreErrors.Conflict("Application connection revision is stale.");
            }

            RejectDuplicateWebhookKey(connection);
            if (_rows.TryUpdate(connection.AgentInstanceId, connection, current))
            {
                return ValueTask.FromResult(connection);
            }
        }
    }

    public ValueTask<ApplicationConnection?> GetByWebhookKeyAsync(
        Guid webhookKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (webhookKey == Guid.Empty)
        {
            return ValueTask.FromResult<ApplicationConnection?>(null);
        }

        var match = _rows.Values.FirstOrDefault(row => row.WebhookKey == webhookKey);
        return ValueTask.FromResult(match);
    }

    private void RejectDuplicateWebhookKey(ApplicationConnection connection)
    {
        if (connection.WebhookKey is not Guid key)
        {
            return;
        }

        if (_rows.Values.Any(row => row.AgentInstanceId != connection.AgentInstanceId && row.WebhookKey == key))
        {
            throw AgentCoreErrors.Conflict("Application connection revision is stale.");
        }
    }
}
