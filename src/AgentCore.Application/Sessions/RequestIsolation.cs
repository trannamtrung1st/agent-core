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
        // These continue the existing request. Their store paths still validate the source
        // configuration task or atomically owned child completion receipt before committing.
        if (run.Admission.Configuration is { } configuration && Required(configuration.Definition)
            && hasPriorRun && run.Admission.Activation.Kind is not (ActivationKind.ConfigurationContinuation or ActivationKind.BackgroundCompleted))
            throw FreshSessionRequired();
    }
}
