using AgentCore.Application.Ports;

namespace AgentCore.Application.Agents;

internal static class ApplicationMessageToolDescriptions
{
    public static ModelToolDefinition WithBudget(ModelToolDefinition definition, ApplicationMessageBudget budget) =>
        definition with
        {
            Description = Describe(budget)
        };

    private static string Describe(ApplicationMessageBudget budget)
    {
        var remaining = budget.RemainingMessages;
        return
            "Send a brief intermediate progress update while substantive work continues. "
            + $"{remaining} intermediate message{(remaining == 1 ? "" : "s")} remain in this execution. "
            + "This is not the final answer; the user turn must still finish with chat.respond. "
            + "The runtime chooses the destination. Do not include a session, recipient, or channel.";
    }
}
