using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Work;

namespace AgentCore.Infrastructure.Persistence;

internal sealed class InMemoryDurableState
{
    public object Gate { get; } = new();

    public Dictionary<Guid, TriggerRegistration> Registrations { get; } = [];

    public Dictionary<Guid, TriggerOccurrence> Occurrences { get; } = [];

    public Dictionary<DedupeIdentity, Guid> DedupeKeys { get; } = [];

    public Dictionary<Guid, WorkItem> WorkItems { get; } = [];

    public Dictionary<string, Guid> AttentionAlerts { get; } = [];

    public Dictionary<Guid, Guid> WorkBySource { get; } = [];

    public Dictionary<Guid, ConversationTurnExecution> TurnExecutions { get; } = [];

    public Dictionary<SourceEventIdentity, Guid> TurnExecutionBySource { get; } = [];

    public readonly record struct DedupeIdentity(Guid AgentInstanceId, Guid ProfileId, string DedupeKey);

    public readonly record struct SourceEventIdentity(Guid SessionId, Guid SourceEventId);
}
