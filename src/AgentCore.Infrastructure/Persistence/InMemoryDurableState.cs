using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Infrastructure.Persistence;

internal sealed class InMemoryDurableState
{
    public object Gate { get; } = new();

    public Dictionary<Guid, Automation> Registrations { get; } = [];

    public Dictionary<Guid, TriggerOccurrence> Occurrences { get; } = [];

    public Dictionary<DedupeIdentity, Guid> DedupeKeys { get; } = [];

    public readonly record struct DedupeIdentity(Guid AgentInstanceId, Guid ProfileId, string DedupeKey);

}
