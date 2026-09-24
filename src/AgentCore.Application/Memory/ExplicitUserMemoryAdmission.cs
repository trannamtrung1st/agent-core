using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Memory;

public static class ExplicitUserMemoryAdmission
{
    public static async ValueTask<ExplicitUserMemoryCaptureOutcome> TryAdmitAsync(
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
        if (!policy.SessionMemory)
        {
            return ExplicitUserMemoryCaptureOutcome.Unavailable;
        }

        if (!ExplicitUserMemoryRequests.TryParse(userText, out var parsed) || parsed is null)
        {
            return ExplicitUserMemoryCaptureOutcome.None;
        }

        var admission = SessionMemoryPrompt.CreateAdmissionContext("user_explicit", definition, profile, entries);
        var owner = new TrustedMemoryOwner(sessionId);
        var proposal = new MemoryWriteProposal(parsed.Kind, parsed.Subject, parsed.Content, [sourceEntryId]);
        var promotedInstanceId = agentInstanceId is Guid instanceValue && instanceValue != Guid.Empty
            ? instanceValue
            : Guid.Empty;
        var wantsCrossSession = policy.IdentityUserPromotion
            && promotedInstanceId != Guid.Empty
            && profile is not null
            && profile.ProfileId != Guid.Empty;

        StructuredMemoryItem sessionItem;
        ExplicitUserMemoryCaptureOutcome sessionOutcome;
        var existing = await memories.FindActiveBySubjectAsync(owner, parsed.Kind, parsed.Subject, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            try
            {
                sessionItem = await memories.WriteAsync(owner, proposal, admission, cancellationToken)
                    .ConfigureAwait(false);
                sessionOutcome = ExplicitUserMemoryCaptureOutcome.Stored;
            }
            catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
            {
                logger.LogDebug(ex, "Explicit user memory rejected for session {SessionId}", sessionId);
                return ExplicitUserMemoryCaptureOutcome.Rejected;
            }
        }
        else if (string.Equals(existing.Content, parsed.Content, StringComparison.Ordinal))
        {
            sessionItem = existing;
            sessionOutcome = ExplicitUserMemoryCaptureOutcome.AlreadyStored;
        }
        else
        {
            try
            {
                sessionItem = await memories.UpdateAsync(
                    owner,
                    new MemoryUpdateProposal(existing.MemoryId, parsed.Subject, parsed.Content, [sourceEntryId]),
                    admission,
                    cancellationToken).ConfigureAwait(false);
                sessionOutcome = ExplicitUserMemoryCaptureOutcome.Updated;
            }
            catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
            {
                logger.LogDebug(ex, "Explicit user memory update rejected for session {SessionId}", sessionId);
                return ExplicitUserMemoryCaptureOutcome.Rejected;
            }
        }

        if (!wantsCrossSession)
        {
            LogSuccess(logger, sessionId, parsed.Subject, sessionOutcome);
            return sessionOutcome;
        }

        var identityOwner = new TrustedIdentityUserOwner(promotedInstanceId, profile!.ProfileId);
        var identitySynced = await SyncIdentityUserAsync(
            memories,
            owner,
            sessionItem,
            identityOwner,
            [sourceEntryId],
            admission,
            logger,
            sessionId,
            cancellationToken).ConfigureAwait(false);

        var outcome = MapCrossSessionOutcome(sessionOutcome, identitySynced);
        if (outcome is ExplicitUserMemoryCaptureOutcome.Stored
            or ExplicitUserMemoryCaptureOutcome.Updated
            or ExplicitUserMemoryCaptureOutcome.AlreadyStored)
        {
            LogSuccess(logger, sessionId, parsed.Subject, outcome);
        }

        return outcome;
    }

    private static ExplicitUserMemoryCaptureOutcome MapCrossSessionOutcome(
        ExplicitUserMemoryCaptureOutcome sessionOutcome,
        bool identitySynced) =>
        identitySynced
            ? sessionOutcome
            : sessionOutcome switch
            {
                ExplicitUserMemoryCaptureOutcome.Stored => ExplicitUserMemoryCaptureOutcome.StoredSessionOnly,
                ExplicitUserMemoryCaptureOutcome.Updated => ExplicitUserMemoryCaptureOutcome.UpdatedSessionOnly,
                ExplicitUserMemoryCaptureOutcome.AlreadyStored => ExplicitUserMemoryCaptureOutcome.StoredSessionOnly,
                _ => sessionOutcome
            };

    private static void LogSuccess(
        ILogger logger,
        Guid sessionId,
        string subject,
        ExplicitUserMemoryCaptureOutcome outcome) =>
        logger.LogInformation(
            "Captured explicit user memory for session {SessionId} subject {Subject} outcome {Outcome}",
            sessionId,
            subject,
            outcome);

    private static async ValueTask<bool> SyncIdentityUserAsync(
        IStructuredMemoryService memories,
        TrustedMemoryOwner sessionOwner,
        StructuredMemoryItem sessionItem,
        TrustedIdentityUserOwner identityOwner,
        IReadOnlyList<Guid> sourceEntryIds,
        MemoryAdmissionContext admission,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var existing = await memories.FindActiveIdentityUserBySubjectAsync(
            identityOwner,
            sessionItem.Kind,
            sessionItem.Subject,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            try
            {
                await memories.PromoteToIdentityUserAsync(
                    sessionOwner,
                    sessionItem.MemoryId,
                    identityOwner,
                    promotionAllowed: true,
                    admission,
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (AgentCoreException ex) when (ex.Code == "Conflict")
            {
                existing = await memories.FindActiveIdentityUserBySubjectAsync(
                    identityOwner,
                    sessionItem.Kind,
                    sessionItem.Subject,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    logger.LogDebug(ex, "Identity memory promotion conflict without lookup for session {SessionId}", sessionId);
                    return false;
                }
            }
            catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected")
            {
                logger.LogDebug(ex, "Identity memory promotion skipped for session {SessionId}", sessionId);
                return false;
            }
        }

        if (existing is not null
            && string.Equals(existing.Content, sessionItem.Content, StringComparison.Ordinal))
        {
            return true;
        }

        if (existing is null)
        {
            return false;
        }

        try
        {
            await memories.UpdateIdentityUserAsync(
                identityOwner,
                new MemoryUpdateProposal(
                    existing.MemoryId,
                    sessionItem.Subject,
                    sessionItem.Content,
                    sourceEntryIds),
                retrievalAllowed: true,
                admission,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "PolicyDenied" or "MemoryRejected")
        {
            logger.LogDebug(ex, "Identity memory update skipped for session {SessionId}", sessionId);
            return false;
        }
    }
}
