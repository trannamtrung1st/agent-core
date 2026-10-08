using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Tests;

public sealed class AgentStepControllerTests
{
    [Theory]
    [InlineData(AgentStepDisposition.Complete)]
    [InlineData(AgentStepDisposition.Continue)]
    public void Chat_disposition_delivers_once_without_another_generation_or_state_mutation(AgentStepDisposition disposition)
    {
        var decision = AgentStepController.Decide(Step(disposition, includeChat: true, includeMemory: true));

        Assert.Equal(AgentStepEffect.DeliverChatOnce, decision.Effect);
        Assert.NotNull(decision.Chat);
        Assert.Equal("Shown", decision.Chat.DisplayText);
        Assert.False(decision.ScheduleAnotherGeneration);
        Assert.True(decision.ExecuteChat);
        Assert.True(decision.StageMemoryAfterChatSuccess);
        Assert.True(decision.SuccessfulAssistantCompletion);
        AssertNoDirectMutation(decision);
    }

    [Theory]
    [InlineData(AgentStepDisposition.Complete)]
    [InlineData(AgentStepDisposition.Continue)]
    public void Zero_chat_actions_return_control_without_chat_or_memory(AgentStepDisposition disposition)
    {
        var decision = AgentStepController.Decide(Step(disposition, includeChat: false, includeMemory: true));

        Assert.Equal(AgentStepEffect.ReturnWithoutChat, decision.Effect);
        Assert.Null(decision.Chat);
        Assert.False(decision.ScheduleAnotherGeneration);
        Assert.False(decision.ExecuteChat);
        Assert.False(decision.StageMemoryAfterChatSuccess);
        Assert.False(decision.SuccessfulAssistantCompletion);
        AssertNoDirectMutation(decision);
    }

    [Fact]
    public void Wait_executes_nothing_even_when_chat_and_memory_were_requested()
    {
        var decision = AgentStepController.Decide(Step(AgentStepDisposition.Wait, includeChat: true, includeMemory: true));

        Assert.Equal(AgentStepEffect.WaitForExternalInput, decision.Effect);
        Assert.Null(decision.Chat);
        Assert.False(decision.ScheduleAnotherGeneration);
        Assert.False(decision.ExecuteChat);
        Assert.False(decision.StageMemoryAfterChatSuccess);
        Assert.False(decision.SuccessfulAssistantCompletion);
        AssertNoDirectMutation(decision);
    }

    [Fact]
    public void Blocked_is_not_a_successful_assistant_completion()
    {
        var decision = AgentStepController.Decide(Step(AgentStepDisposition.Blocked, includeChat: true, includeMemory: true));

        Assert.Equal(AgentStepEffect.BlockActivation, decision.Effect);
        Assert.Null(decision.Chat);
        Assert.False(decision.ExecuteChat);
        Assert.False(decision.StageMemoryAfterChatSuccess);
        Assert.False(decision.SuccessfulAssistantCompletion);
        AssertNoDirectMutation(decision);
    }

    [Fact]
    public void Undefined_disposition_does_not_fall_through_to_chat()
    {
        var decision = AgentStepController.Decide(Step((AgentStepDisposition)99, includeChat: true, includeMemory: true));

        Assert.Equal(AgentStepEffect.BlockActivation, decision.Effect);
        Assert.False(decision.ExecuteChat);
        Assert.False(decision.SuccessfulAssistantCompletion);
        AssertNoDirectMutation(decision);
    }

    private static void AssertNoDirectMutation(AgentStepControllerDecision decision)
    {
        Assert.False(decision.MutatesSessionLifecycle);
        Assert.False(decision.MutatesAgentRun);
        Assert.False(decision.MutatesApproval);
        Assert.False(decision.MutatesTrigger);
        Assert.False(decision.MutatesMemoryDirectly);
    }

    private static AgentStep Step(AgentStepDisposition disposition, bool includeChat, bool includeMemory)
    {
        IReadOnlyList<AgentAction> actions = includeChat
            ? [new ChatRespondAction("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), [])]
            : [];
        IReadOnlyList<MemoryProposal> memory = includeMemory
            ?
            [
                new MemoryProposal(
                    MemoryProposalOperation.Upsert,
                    MemoryKind.Fact,
                    "editor",
                    "Rider",
                    MemoryScopeHint.User,
                    MemoryProposalSource.AgentInferred)
            ]
            : [];
        return new AgentStep(disposition, actions, memory);
    }
}
