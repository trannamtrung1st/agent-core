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
        StructuredMemoryItem sessionItem;
        ExplicitUserMemoryCaptureOutcome outcome;
        try
        {
            sessionItem = await memories.WriteAsync(owner, proposal, admission, cancellationToken).ConfigureAwait(false);
            outcome = ExplicitUserMemoryCaptureOutcome.Stored;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            var existing = await FindSessionActiveBySubjectAsync(memories, owner, parsed, admission, cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                logger.LogDebug(
                    "Explicit user memory conflict without an active item for subject {Subject} in session {SessionId}",
                    parsed.Subject,
                    sessionId);
                return ExplicitUserMemoryCaptureOutcome.Rejected;
            }

            if (string.Equals(existing.Content, parsed.Content, StringComparison.Ordinal))
            {
                sessionItem = existing;
                outcome = ExplicitUserMemoryCaptureOutcome.AlreadyStored;
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
                    outcome = ExplicitUserMemoryCaptureOutcome.Updated;
                }
                catch (AgentCoreException updateEx) when (updateEx.Code is "MemoryRejected" or "MemoryCapacity")
                {
                    logger.LogDebug(updateEx, "Explicit user memory update rejected for session {SessionId}", sessionId);
                    return ExplicitUserMemoryCaptureOutcome.Rejected;
                }
            }
        }
        catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
        {
            logger.LogDebug(ex, "Explicit user memory rejected for session {SessionId}", sessionId);
            return ExplicitUserMemoryCaptureOutcome.Rejected;
        }

        if (policy.IdentityUserPromotion
            && agentInstanceId is Guid instanceId
            && instanceId != Guid.Empty
            && profile is not null
            && profile.ProfileId != Guid.Empty)
        {
            await SyncIdentityUserAsync(
                memories,
                owner,
                sessionItem,
                new TrustedIdentityUserOwner(instanceId, profile.ProfileId),
                [sourceEntryId],
                admission,
                logger,
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        if (outcome is ExplicitUserMemoryCaptureOutcome.Stored or ExplicitUserMemoryCaptureOutcome.Updated)
        {
            logger.LogInformation(
                "Captured explicit user memory for session {SessionId} subject {Subject} outcome {Outcome}",
                sessionId,
                parsed.Subject,
                outcome);
        }

        return outcome;
    }

    private static async ValueTask<StructuredMemoryItem?> FindSessionActiveBySubjectAsync(
        IStructuredMemoryService memories,
        TrustedMemoryOwner owner,
        ExplicitUserMemoryRequests.Parsed parsed,
        MemoryAdmissionContext admission,
        CancellationToken cancellationToken)
    {
        var subjectKey = StructuredMemoryItem.SubjectKeyFor(StructuredMemoryItem.CollapseSubject(parsed.Subject));
        var items = await memories.SearchAsync(owner, new MemorySearchQuery(null, parsed.Kind), admission, cancellationToken)
            .ConfigureAwait(false);
        return items.FirstOrDefault(item => item.SubjectKey == subjectKey);
    }

    private static async ValueTask SyncIdentityUserAsync(
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
        var identityItems = await memories.SearchIdentityUserAsync(
            identityOwner,
            new MemorySearchQuery(null, sessionItem.Kind),
            retrievalAllowed: true,
            admission,
            cancellationToken).ConfigureAwait(false);
        var existing = identityItems.FirstOrDefault(item => item.SubjectKey == sessionItem.SubjectKey);
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
            }
            catch (AgentCoreException ex) when (ex.Code is "Conflict" or "PolicyDenied" or "MemoryRejected")
            {
                logger.LogDebug(ex, "Identity memory promotion skipped for session {SessionId}", sessionId);
            }

            return;
        }

        if (string.Equals(existing.Content, sessionItem.Content, StringComparison.Ordinal))
        {
            return;
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
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "PolicyDenied" or "MemoryRejected")
        {
            logger.LogDebug(ex, "Identity memory update skipped for session {SessionId}", sessionId);
        }
    }
}
