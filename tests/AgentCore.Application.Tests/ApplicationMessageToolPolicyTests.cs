using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class ApplicationMessageToolPolicyTests
{
    [Theory]
    [InlineData(ToolCatalog.WorkspaceList, true)]
    [InlineData(ToolCatalog.WebSearch, true)]
    [InlineData("browser.navigate", true)]
    [InlineData("sandbox.run", false)]
    [InlineData(ToolCatalog.SandboxExec, true)]
    [InlineData(ToolCatalog.SkillsLoad, false)]
    [InlineData(ToolCatalog.AppMessageSend, false)]
    public void Substantive_work_tools_unlock_intermediate_messaging(string toolName, bool unlocks) =>
        Assert.Equal(unlocks, ApplicationMessageToolPolicy.UnlocksIntermediateMessaging(toolName));

    [Theory]
    [InlineData(ExecutionOrigin.UserMessage, true, AgentRunOutputContract.ConversationResponse, false, true)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, true, AgentRunOutputContract.ConversationResponse, false, true)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, false, AgentRunOutputContract.ConversationResponse, false, false)]
    [InlineData(ExecutionOrigin.UserMessage, true, AgentRunOutputContract.BackgroundOutcome, false, false)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, true, AgentRunOutputContract.CompletionReport, false, false)]
    [InlineData(ExecutionOrigin.ApprovalContinuation, true, AgentRunOutputContract.ConversationResponse, true, false)]
    [InlineData(ExecutionOrigin.ManualInvocation, true, AgentRunOutputContract.ConversationResponse, true, false)]
    [InlineData(ExecutionOrigin.ScheduledOccurrence, false, AgentRunOutputContract.ConversationResponse, true, false)]
    [InlineData(ExecutionOrigin.ImmediateBackground, true, AgentRunOutputContract.BackgroundOutcome, false, false)]
    [InlineData(ExecutionOrigin.ConfigurationContinuation, true, AgentRunOutputContract.ConversationResponse, false, false)]
    public void Progress_messages_keep_actual_human_conversation_origin(ExecutionOrigin origin, bool human, AgentRunOutputContract output, bool automation, bool expected)
    {
        var activation = Guid.NewGuid();
        var continuation = origin is ExecutionOrigin.ApprovalContinuation or ExecutionOrigin.ConfigurationContinuation or ExecutionOrigin.ImmediateBackground;
        var context = new TrustedExecutionContext(Guid.NewGuid(), Guid.NewGuid(), human ? Guid.NewGuid() : null, Guid.NewGuid(), Guid.NewGuid(),
            activation, continuation ? Guid.NewGuid() : activation, continuation ? Guid.NewGuid() : null, continuation ? Guid.NewGuid() : null, origin);
        Assert.Equal(expected, ApplicationMessageToolPolicy.AllowsConversation(context, output, automation ? new("Exact task", AgentRunTaskSource.Automation, "1") : null));
        Assert.False(ApplicationMessageToolPolicy.AllowsConversation(null, output, null));
    }
}
