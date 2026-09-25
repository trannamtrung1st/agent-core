using System.Collections.Concurrent;

namespace AgentCore.Infrastructure.Definitions;

internal static class DefinitionPublicationLockRegistry
{
    private static readonly ConcurrentDictionary<(string DefinitionId, int Version), object> Locks = new();

    internal static object For(string definitionId, int version) =>
        Locks.GetOrAdd((definitionId, version), static _ => new object());
}
