using System.Collections.Concurrent;

namespace AgentCore.Infrastructure.Definitions;

internal static class DefinitionDraftLockRegistry
{
    private static readonly ConcurrentDictionary<Guid, object> Locks = new();

    internal static object For(Guid draftId) => Locks.GetOrAdd(draftId, static _ => new object());
}
