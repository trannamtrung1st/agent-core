using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Sessions;

public static class RequestIsolation
{
    public static bool Required(AgentDefinition definition) => definition.StateIsolationPolicy?.ConversationContextScope == ConversationContextScope.CurrentRequest;
    public static AgentCoreException FreshSessionRequired() => new("fresh_session_required",
        "This agent requires a new Session for each request. Choose New chat to send another request; this Session's history and audit remain available.", 409);

    public static void ValidateAdmission(AgentRun run, bool hasPriorRun)
    {
        if (run.Admission.Configuration is { } configuration && Required(configuration.Definition)
            && hasPriorRun && run.Admission.Activation.Kind != ActivationKind.ConfigurationContinuation)
            throw FreshSessionRequired();
    }
}
