using System.Collections.Concurrent;

namespace AgentCore.Infrastructure.Definitions;

internal static class AdminOperationLockRegistry
{
    private static readonly ConcurrentDictionary<Guid, object> Locks = new();

    internal static object For(Guid operationId) => Locks.GetOrAdd(operationId, static _ => new object());
}
