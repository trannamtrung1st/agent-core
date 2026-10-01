using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class ApplicationMessageBudgetTests
{
    [Fact]
    public void FromSnapshot_reflects_persisted_messages_for_recovery()
    {
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000aa");
        var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var entries = Enumerable.Range(1, 11)
            .Select(index => new ConversationEntry(
                Guid.Parse($"019944af-00ef-7000-8000-{index:D12}"),
                index,
                null,
                ConversationRole.ApplicationMessage,
                $"msg-{index}",
                responseId,
                EntryStatus.Completed,
                SessionMode.Text,
                0,
                5,
                now,
                ApplicationMessageEffectKey: $"v1:{responseId:N}:m{index}"))
            .ToArray();

        var budget = ApplicationMessageBudget.FromSnapshot(entries, responseId);
        Assert.Equal(1, budget.RemainingMessages);
        Assert.True(budget.CanAdmit(100));
        Assert.False(budget.CanAdmit(8000));
    }

    [Fact]
    public void Aggregate_character_budget_is_enforced_independently_of_count()
    {
        var policy = ApplicationMessagePolicy.Default;
        var budget = new ApplicationMessageBudget(0, 7990, policy);
        Assert.True(budget.CanAdmit(10));
        Assert.False(budget.CanAdmit(11));
        Assert.True(budget.CanOfferTool);
        var exhausted = new ApplicationMessageBudget(0, 8000, policy);
        Assert.False(exhausted.CanOfferTool);
        Assert.False(exhausted.CanAdmit(1));
    }

    [Fact]
    public void Four_max_length_messages_exhaust_aggregate_budget()
    {
        var responseId = Guid.Parse("019944af-00ee-7000-8000-0000000000bb");
        var now = DateTimeOffset.Parse("2026-09-30T12:00:00Z");
        var text = new string('x', ApplicationMessageLimits.MaxCharacters);
        var entries = Enumerable.Range(1, 4)
            .Select(index => new ConversationEntry(
                Guid.Parse($"019944af-00f0-7000-8000-{index:D12}"),
                index,
                null,
                ConversationRole.ApplicationMessage,
                text,
                responseId,
                EntryStatus.Completed,
                SessionMode.Text,
                0,
                text.Length,
                now,
                ApplicationMessageEffectKey: $"v1:{responseId:N}:m{index}"))
            .ToArray();

        var budget = ApplicationMessageBudget.FromSnapshot(entries, responseId);
        Assert.Equal(0, budget.RemainingCharacters);
        Assert.False(budget.CanAdmit(1));
        Assert.Contains("over_budget", ApplicationMessageAdmission.OverBudget(budget), StringComparison.Ordinal);
    }
}
