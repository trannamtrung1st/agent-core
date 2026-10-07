using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;

namespace AgentCore.Api;

internal static class SessionCreateHttp
{
    internal static async Task<SessionSnapshot> CreateAsync(
        CreateSessionRequest body,
        SessionManager sessions,
        CancellationToken cancellationToken,
        ModelSelectionSource modelSource,
        SessionPurpose? purpose = null,
        SessionCompletionPolicy? policy = null,
        TimeSpan? maxDuration = null)
    {
        if (body.AgentInstanceId is not Guid instanceId || instanceId == Guid.Empty)
            throw AgentCoreErrors.Validation("agentInstanceId is required.");
        return await sessions.CreateForInstanceAsync(instanceId, HttpMapping.ParseMode(body.Mode), cancellationToken,
            purpose, policy, maxDuration, body.SpeechLocale, body.Model?.Key, body.Model?.ReasoningEffort, modelSource)
            .ConfigureAwait(false);
    }
}
