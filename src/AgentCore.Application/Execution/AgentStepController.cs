namespace AgentCore.Application.Execution;

public enum AgentStepEffect
{
    DeliverChatOnce,
    ReturnWithoutChat,
    WaitForExternalInput,
    BlockActivation
}

/// <summary>
/// Controller reading of an accepted step. None of these flags mutate Session, AgentRun,
/// approval, trigger, or memory. Memory may be staged only after a Chat action succeeds.
/// </summary>
public sealed record AgentStepControllerDecision(
    AgentStepEffect Effect,
    ChatRespondAction? Chat,
    bool ScheduleAnotherGeneration,
    bool ExecuteChat,
    bool StageMemoryAfterChatSuccess,
    bool SuccessfulAssistantCompletion,
    bool MutatesSessionLifecycle,
    bool MutatesAgentRun,
    bool MutatesApproval,
    bool MutatesTrigger,
    bool MutatesMemoryDirectly);

public static class AgentStepController
{
    public static AgentStepControllerDecision Decide(AgentStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Actions.Any(action => action is not ChatRespondAction)
            || step.Actions.OfType<ChatRespondAction>().Count() > 1)
        {
            return Inactive(AgentStepEffect.BlockActivation);
        }

        var chat = step.Actions.OfType<ChatRespondAction>().SingleOrDefault();
        return step.Disposition switch
        {
            AgentStepDisposition.Wait => Inactive(AgentStepEffect.WaitForExternalInput),
            AgentStepDisposition.Blocked => Inactive(AgentStepEffect.BlockActivation),
            AgentStepDisposition.Complete when chat is not null => Deliver(chat),
            AgentStepDisposition.Continue when chat is not null => Deliver(chat),
            AgentStepDisposition.Complete or AgentStepDisposition.Continue =>
                Inactive(AgentStepEffect.ReturnWithoutChat),
            _ => Inactive(AgentStepEffect.BlockActivation)
        };
    }

    private static AgentStepControllerDecision Deliver(ChatRespondAction chat) =>
        new(
            AgentStepEffect.DeliverChatOnce,
            chat,
            ScheduleAnotherGeneration: false,
            ExecuteChat: true,
            StageMemoryAfterChatSuccess: true,
            SuccessfulAssistantCompletion: true,
            MutatesSessionLifecycle: false,
            MutatesAgentRun: false,
            MutatesApproval: false,
            MutatesTrigger: false,
            MutatesMemoryDirectly: false);

    private static AgentStepControllerDecision Inactive(AgentStepEffect effect) =>
        new(
            effect,
            Chat: null,
            ScheduleAnotherGeneration: false,
            ExecuteChat: false,
            StageMemoryAfterChatSuccess: false,
            SuccessfulAssistantCompletion: false,
            MutatesSessionLifecycle: false,
            MutatesAgentRun: false,
            MutatesApproval: false,
            MutatesTrigger: false,
            MutatesMemoryDirectly: false);
}
