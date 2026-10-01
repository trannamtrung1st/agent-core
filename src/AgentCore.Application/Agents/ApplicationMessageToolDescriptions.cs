using AgentCore.Application.Ports;

namespace AgentCore.Application.Agents;

internal static class ApplicationMessageToolDescriptions
{
    public static ModelToolDefinition WithBudget(ModelToolDefinition definition, ApplicationMessageBudget budget) =>
        definition with
        {
            Description = Describe(budget),
            ParametersJson = ParametersJson(budget.EffectiveMaxTextLengthForOffer)
        };

    private static string Describe(ApplicationMessageBudget budget)
    {
        var remainingMessages = budget.RemainingMessages;
        var remainingCharacters = budget.RemainingCharacters;
        return
            "Send a brief intermediate progress update while substantive work continues. "
            + $"{remainingMessages} intermediate message{(remainingMessages == 1 ? "" : "s")} remain in this execution. "
            + $"{remainingCharacters} aggregate characters remain. "
            + "This is not the final answer; the user turn must still finish with chat.respond. "
            + "The runtime chooses the destination. Do not include a session, recipient, or channel.";
    }

    private static string ParametersJson(int maxLength) =>
        $"{{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{{\"text\":{{\"type\":\"string\",\"minLength\":1,\"maxLength\":{maxLength}}}}},\"required\":[\"text\"]}}";
}
