using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Conversation;

public static class ConversationTurnExecutionFactory
{
    public static ConversationTurnExecution ForAcceptedUserTurn(
        Guid executionId,
        Guid responseId,
        SessionSnapshot snapshot,
        ConversationEntry userEntry,
        DateTimeOffset acceptedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(userEntry);
        var definition = snapshot.Definition
            ?? throw new InvalidOperationException("Session definition is required for conversational execution.");
        var model = snapshot.ModelSelection
            ?? throw new InvalidOperationException("Session model selection is required for conversational execution.");
        var pin = new WorkModelPin(
            model.CatalogKey,
            model.ProviderAlias,
            model.ModelId,
            model.ReasoningEffort);
        return ConversationTurnExecution.AcceptNew(
            executionId,
            snapshot.SessionId,
            userEntry.EntryId,
            userEntry.SourceEventId ?? userEntry.EntryId,
            responseId,
            snapshot.AgentInstanceId,
            snapshot.ProfileId,
            definition.Id,
            definition.Version,
            snapshot.PinnedPersona,
            pin,
            acceptedAtUtc);
    }
}
