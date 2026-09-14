using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Ports;

public interface IAgentDefinitionStore
{
    ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default);

    ValueTask<AgentDefinition?> GetAsync(
        string id,
        int? version = null,
        CancellationToken cancellationToken = default);
}

public interface IMemoryStore
{
    ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        SessionSnapshot snapshot,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default);

    ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default);

    ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default);
}

public interface IIdGenerator
{
    Guid NewId();
    Guid NewSessionId();
}

public sealed record EnvironmentEvent(
    Guid EventId,
    string Kind,
    IReadOnlyDictionary<string, string> Data);

public interface IEnvironmentEventIngress
{
    ValueTask PublishAsync(Guid sessionId, EnvironmentEvent input, CancellationToken cancellationToken = default);
}
