using System.Collections.Concurrent;

namespace AgentCore.Application.Admin;

public static class AdminOperationIdGate
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();

    public static SemaphoreSlim Acquire(Guid operationId) =>
        Gates.GetOrAdd(operationId, static _ => new SemaphoreSlim(1, 1));
}
