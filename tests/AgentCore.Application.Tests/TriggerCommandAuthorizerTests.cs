using AgentCore.Application.Triggers;

namespace AgentCore.Application.Tests;

public sealed class TriggerCommandAuthorizerTests
{
    private static readonly HeuristicTriggerCommandAuthorizer Authorizer = new();

    [Theory]
    [InlineData("say hello to me in 1 minute", TriggerCommandAction.Create)]
    [InlineData("say hello to me after 1 minute", TriggerCommandAction.Create)]
    [InlineData("set a reminder tomorrow at 9", TriggerCommandAction.Create)]
    [InlineData("ping me in five minutes", TriggerCommandAction.Create)]
    [InlineData("every Friday remind me to submit the report", TriggerCommandAction.Create)]
    [InlineData("1 phút nữa chào tôi", TriggerCommandAction.Create)]
    public async Task Allows_explicit_create_requests(string text, TriggerCommandAction action)
    {
        var decision = await Authorizer.AuthorizeCurrentTurnAsync(text, "vi-VN", action);
        Assert.Equal(TriggerCommandAuthorizationDecision.Allow, decision);
    }

    [Theory]
    [InlineData("I'll be back in 5 minutes")]
    [InlineData("I have a meeting tomorrow")]
    [InlineData("Can you schedule reminders?")]
    [InlineData("What happens tomorrow?")]
    [InlineData("Why did you schedule that?")]
    [InlineData("don't create a reminder")]
    public async Task Denies_create_false_positives(string text)
    {
        var decision = await Authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Create);
        Assert.Equal(TriggerCommandAuthorizationDecision.Deny, decision);
    }

    [Fact]
    public async Task Whats_on_my_schedule_is_list_only()
    {
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await Authorizer.AuthorizeCurrentTurnAsync("What's on my schedule tomorrow?", "en", TriggerCommandAction.List));
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Deny,
            await Authorizer.AuthorizeCurrentTurnAsync("What's on my schedule tomorrow?", "en", TriggerCommandAction.Create));
    }

    [Theory]
    [InlineData("show my reminders")]
    [InlineData("what do I have scheduled?")]
    public async Task Allows_list_requests(string text)
    {
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await Authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.List));
    }

    [Theory]
    [InlineData("move it to 10")]
    [InlineData("move that reminder to 10")]
    public async Task Allows_update_requests(string text)
    {
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await Authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Update));
    }

    [Theory]
    [InlineData("delete that reminder")]
    [InlineData("cancel that reminder")]
    public async Task Allows_cancel_requests(string text)
    {
        Assert.Equal(
            TriggerCommandAuthorizationDecision.Allow,
            await Authorizer.AuthorizeCurrentTurnAsync(text, "en", TriggerCommandAction.Cancel));
    }
}
