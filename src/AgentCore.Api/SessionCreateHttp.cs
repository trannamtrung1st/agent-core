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
        var hasInstance = body.AgentInstanceId is Guid instanceId && instanceId != Guid.Empty;
        var hasAgent = !string.IsNullOrWhiteSpace(body.AgentId);
        if (hasInstance == hasAgent)
        {
            throw AgentCoreErrors.Validation("Exactly one of agentId or agentInstanceId is required.");
        }

        var mode = HttpMapping.ParseMode(body.Mode);
        if (hasInstance)
        {
            return await sessions.CreateForInstanceAsync(
                    body.AgentInstanceId!.Value,
                    mode,
                    cancellationToken,
                    purpose,
                    policy,
                    maxDuration,
                    body.SpeechLocale,
                    body.Model?.Key,
                    body.Model?.ReasoningEffort,
                    modelSource)
                .ConfigureAwait(false);
        }

        return await sessions.CreateAsync(
                body.AgentId!,
                body.AgentVersion,
                mode,
                cancellationToken,
                purpose,
                policy,
                maxDuration,
                body.SpeechLocale,
                body.Model?.Key,
                body.Model?.ReasoningEffort,
                modelSource)
            .ConfigureAwait(false);
    }
}
