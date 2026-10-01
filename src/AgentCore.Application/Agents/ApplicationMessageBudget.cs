using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Agents;

public readonly record struct ApplicationMessageBudget(
    int AdmittedCount,
    int AdmittedCharacters,
    ApplicationMessagePolicy Policy)
{
    public int RemainingMessages => Math.Max(0, Policy.MaxAdmittedPerExecution - AdmittedCount);

    public int RemainingCharacters => Math.Max(0, Policy.MaxAggregateCharactersPerExecution - AdmittedCharacters);

    public bool IsExhausted => RemainingMessages == 0 || RemainingCharacters == 0;

    public bool CanOfferTool => !IsExhausted;

    public bool CanAdmit(int textLength) =>
        textLength > 0
        && textLength <= Policy.MaxCharactersPerMessage
        && RemainingMessages > 0
        && AdmittedCharacters + textLength <= Policy.MaxAggregateCharactersPerExecution;

    public ApplicationMessageBudget AfterAdmit(int textLength) =>
        new(AdmittedCount + 1, AdmittedCharacters + textLength, Policy);

    public static ApplicationMessageBudget Fresh(ApplicationMessagePolicy? policy = null) =>
        new(0, 0, policy ?? ApplicationMessagePolicy.Default);

    public static ApplicationMessageBudget FromSnapshot(
        IEnumerable<ConversationEntry> entries,
        Guid responseId,
        ApplicationMessagePolicy? policy = null)
    {
        policy ??= ApplicationMessagePolicy.Default;
        var admitted = entries
            .Where(entry =>
                entry.Role == ConversationRole.ApplicationMessage
                && entry.ResponseId == responseId
                && entry.Status == EntryStatus.Completed)
            .ToArray();
        var characters = admitted.Sum(entry => entry.Text?.Length ?? 0);
        return new ApplicationMessageBudget(admitted.Length, characters, policy);
    }
}
