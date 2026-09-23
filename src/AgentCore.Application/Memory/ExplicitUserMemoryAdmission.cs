using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Memory;

public static class ExplicitUserMemoryAdmission
{
    public static async ValueTask TryAdmitAsync(
        IStructuredMemoryService memories,
        AgentDefinition definition,
        Guid sessionId,
        Guid? agentInstanceId,
        UserProfile? profile,
        IReadOnlyList<ConversationEntry> entries,
        Guid sourceEntryId,
        string userText,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var policy = definition.MemoryPolicy ?? MemoryPolicy.Disabled;
        if (!policy.SessionMemory || !ExplicitUserMemoryRequests.TryParse(userText, out var parsed) || parsed is null)
        {
            return;
        }

        var admission = SessionMemoryPrompt.CreateAdmissionContext("user_explicit", definition, profile, entries);
        var owner = new TrustedMemoryOwner(sessionId);
        var proposal = new MemoryWriteProposal(parsed.Kind, parsed.Subject, parsed.Content, [sourceEntryId]);
        StructuredMemoryItem written;
        try
        {
            written = await memories.WriteAsync(owner, proposal, admission, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            logger.LogDebug(
                "Explicit user memory already exists for subject {Subject} in session {SessionId}",
                parsed.Subject,
                sessionId);
            return;
        }
        catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
        {
            logger.LogDebug(ex, "Explicit user memory rejected for session {SessionId}", sessionId);
            return;
        }

        if (policy.IdentityUserPromotion
            && agentInstanceId is Guid instanceId
            && instanceId != Guid.Empty
            && profile is not null
            && profile.ProfileId != Guid.Empty)
        {
            try
            {
                await memories.PromoteToIdentityUserAsync(
                    owner,
                    written.MemoryId,
                    new TrustedIdentityUserOwner(instanceId, profile.ProfileId),
                    promotionAllowed: true,
                    admission,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AgentCoreException ex) when (ex.Code is "Conflict" or "PolicyDenied" or "MemoryRejected")
            {
                logger.LogDebug(ex, "Identity memory promotion skipped for session {SessionId}", sessionId);
            }
        }

        logger.LogInformation(
            "Captured explicit user memory for session {SessionId} subject {Subject}",
            sessionId,
            parsed.Subject);
    }
}
