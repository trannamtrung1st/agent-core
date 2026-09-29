using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Memory;

public static class MemoryAdmission
{
    public static async ValueTask<IReadOnlyList<MemoryAdmissionResult>> AdmitAsync(
        IStructuredMemoryService memories,
        AgentDefinition definition,
        Guid sessionId,
        Guid? agentInstanceId,
        UserProfile? profile,
        IReadOnlyList<ConversationEntry> entries,
        Guid sourceEntryId,
        IReadOnlyList<MemoryProposal> proposals,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (proposals.Count == 0)
        {
            return [];
        }

        var results = new List<MemoryAdmissionResult>(Math.Min(proposals.Count, MemoryProposalCodec.MaxProposalsPerTurn));
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            if (index >= MemoryProposalCodec.MaxProposalsPerTurn)
            {
                results.Add(new MemoryAdmissionResult(MemoryAdmissionStatus.Rejected, proposal));
                continue;
            }

            var status = await AdmitOneAsync(
                memories,
                definition,
                sessionId,
                agentInstanceId,
                profile,
                entries,
                sourceEntryId,
                proposal,
                logger,
                cancellationToken).ConfigureAwait(false);
            results.Add(new MemoryAdmissionResult(status, proposal));
        }

        return results;
    }

    public static async ValueTask<MemoryAdmissionStatus> AdmitOneAsync(
        IStructuredMemoryService memories,
        AgentDefinition definition,
        Guid sessionId,
        Guid? agentInstanceId,
        UserProfile? profile,
        IReadOnlyList<ConversationEntry> entries,
        Guid sourceEntryId,
        MemoryProposal proposal,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        var policy = EffectiveConfigurationComposer.MemoryPolicyOf(definition);
        if (!policy.SessionMemory)
        {
            return MemoryAdmissionStatus.Unavailable;
        }

        if (!IsShapeValid(proposal))
        {
            return MemoryAdmissionStatus.Rejected;
        }

        var sourceIds = sourceEntryId == Guid.Empty ? Array.Empty<Guid>() : new[] { sourceEntryId };
        var admission = SessionMemoryPrompt.CreateAdmissionContext(Provenance(proposal.Source), definition, profile, entries);
        var owner = new TrustedMemoryOwner(sessionId);
        var promotedInstanceId = agentInstanceId is Guid instanceValue && instanceValue != Guid.Empty
            ? instanceValue
            : Guid.Empty;
        var allowIdentity = proposal.ScopeHint is not MemoryScopeHint.Session
            && policy.IdentityUserPromotion
            && promotedInstanceId != Guid.Empty
            && profile is not null
            && profile.ProfileId != Guid.Empty;
        var allowUser = proposal.ScopeHint is MemoryScopeHint.User
            && policy.UserPromotion
            && profile is not null
            && profile.ProfileId != Guid.Empty;

        if (proposal.Operation == MemoryProposalOperation.Delete)
        {
            return await DeleteAsync(
                memories,
                owner,
                proposal,
                allowIdentity,
                allowUser,
                promotedInstanceId,
                profile,
                admission,
                logger,
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        StructuredMemoryItem sessionItem;
        MemoryAdmissionStatus sessionOutcome;
        var existing = await memories.FindActiveBySubjectAsync(owner, proposal.Kind, proposal.Subject, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            try
            {
                sessionItem = await memories.WriteAsync(
                    owner,
                    new MemoryWriteProposal(proposal.Kind, proposal.Subject, proposal.Content, sourceIds),
                    admission,
                    cancellationToken).ConfigureAwait(false);
                sessionOutcome = MemoryAdmissionStatus.Stored;
            }
            catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
            {
                logger.LogDebug(ex, "Memory proposal rejected for session {SessionId}", sessionId);
                return MemoryAdmissionStatus.Rejected;
            }
        }
        else if (string.Equals(existing.Content, proposal.Content.Trim(), StringComparison.Ordinal))
        {
            sessionItem = existing;
            sessionOutcome = MemoryAdmissionStatus.AlreadyStored;
        }
        else
        {
            try
            {
                sessionItem = await memories.UpdateAsync(
                    owner,
                    new MemoryUpdateProposal(existing.MemoryId, proposal.Subject, proposal.Content, sourceIds),
                    admission,
                    cancellationToken).ConfigureAwait(false);
                sessionOutcome = MemoryAdmissionStatus.Updated;
            }
            catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "MemoryCapacity")
            {
                logger.LogDebug(ex, "Memory proposal update rejected for session {SessionId}", sessionId);
                return MemoryAdmissionStatus.Rejected;
            }
        }

        if (!allowIdentity && !allowUser)
        {
            return SessionOnly(sessionOutcome);
        }

        var identitySynced = false;
        if (allowIdentity)
        {
            identitySynced = await SyncIdentityUserAsync(
                memories,
                owner,
                sessionItem,
                new TrustedIdentityUserOwner(promotedInstanceId, profile!.ProfileId),
                sourceIds,
                admission,
                logger,
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        var userSynced = false;
        if (allowUser)
        {
            userSynced = await SyncUserAsync(
                memories,
                owner,
                sessionItem,
                new TrustedUserOwner(profile!.ProfileId),
                sourceIds,
                admission,
                logger,
                sessionId,
                cancellationToken).ConfigureAwait(false);
        }

        if (allowIdentity && !identitySynced)
        {
            return SessionOnly(sessionOutcome);
        }

        if (allowUser && !userSynced)
        {
            return SessionOnly(sessionOutcome);
        }

        return sessionOutcome;
    }

    private static bool IsShapeValid(MemoryProposal proposal)
    {
        if (!Enum.IsDefined(proposal.Operation) || !Enum.IsDefined(proposal.Kind) || !Enum.IsDefined(proposal.Source))
        {
            return false;
        }

        var subject = proposal.Subject.Trim();
        if (subject.Length is 0 or > MemoryLimits.MaxSubjectCharacters)
        {
            return false;
        }

        if (proposal.Operation == MemoryProposalOperation.Delete)
        {
            return true;
        }

        var content = proposal.Content.Trim();
        return content.Length is > 0 and <= MemoryLimits.MaxContentCharacters;
    }

    private static MemoryAdmissionStatus SessionOnly(MemoryAdmissionStatus sessionOutcome) =>
        sessionOutcome switch
        {
            MemoryAdmissionStatus.Stored => MemoryAdmissionStatus.StoredSessionOnly,
            MemoryAdmissionStatus.Updated => MemoryAdmissionStatus.UpdatedSessionOnly,
            MemoryAdmissionStatus.AlreadyStored => MemoryAdmissionStatus.StoredSessionOnly,
            MemoryAdmissionStatus.Deleted => MemoryAdmissionStatus.Deleted,
            _ => sessionOutcome
        };

    private static string Provenance(MemoryProposalSource source) => source switch
    {
        MemoryProposalSource.UserExplicit => "user_explicit",
        MemoryProposalSource.Application => "application",
        MemoryProposalSource.Admin => "admin",
        _ => "agent_inferred"
    };

    private static async ValueTask<MemoryAdmissionStatus> DeleteAsync(
        IStructuredMemoryService memories,
        TrustedMemoryOwner owner,
        MemoryProposal proposal,
        bool allowIdentity,
        bool allowUser,
        Guid instanceId,
        UserProfile? profile,
        MemoryAdmissionContext admission,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        var existing = await memories.FindActiveBySubjectAsync(owner, proposal.Kind, proposal.Subject, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return MemoryAdmissionStatus.Rejected;
        }

        try
        {
            await memories.DeleteAsync(owner, existing.MemoryId, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "NotFound")
        {
            logger.LogDebug(ex, "Memory delete rejected for session {SessionId}", sessionId);
            return MemoryAdmissionStatus.Rejected;
        }

        if (allowIdentity && profile is not null)
        {
            var identity = await memories.FindActiveIdentityUserBySubjectAsync(
                new TrustedIdentityUserOwner(instanceId, profile.ProfileId),
                proposal.Kind,
                proposal.Subject,
                cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                try
                {
                    await memories.DeleteIdentityUserAsync(
                        new TrustedIdentityUserOwner(instanceId, profile.ProfileId),
                        identity.MemoryId,
                        retrievalAllowed: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected" or "NotFound")
                {
                    logger.LogDebug(ex, "Identity memory delete skipped for session {SessionId}", sessionId);
                }
            }
        }

        if (allowUser && profile is not null)
        {
            var userItems = await memories.SearchUserAsync(
                new TrustedUserOwner(profile.ProfileId),
                new MemorySearchQuery(proposal.Subject, proposal.Kind),
                retrievalAllowed: true,
                admission,
                cancellationToken).ConfigureAwait(false);
            var match = userItems.FirstOrDefault(item =>
                string.Equals(item.Subject, existing.Subject, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                try
                {
                    await memories.DeleteUserAsync(
                        new TrustedUserOwner(profile.ProfileId),
                        match.MemoryId,
                        retrievalAllowed: true,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected" or "NotFound")
                {
                    logger.LogDebug(ex, "User memory delete skipped for session {SessionId}", sessionId);
                }
            }
        }

        return MemoryAdmissionStatus.Deleted;
    }

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

    private static async ValueTask<bool> SyncUserAsync(
        IStructuredMemoryService memories,
        TrustedMemoryOwner sessionOwner,
        StructuredMemoryItem sessionItem,
        TrustedUserOwner userOwner,
        IReadOnlyList<Guid> sourceEntryIds,
        MemoryAdmissionContext admission,
        ILogger logger,
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        _ = sourceEntryIds;
        try
        {
            await memories.PromoteSessionToUserAsync(
                sessionOwner,
                sessionItem.MemoryId,
                userOwner,
                promotionAllowed: true,
                admission,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AgentCoreException ex) when (ex.Code is "Conflict" or "PolicyDenied" or "MemoryRejected")
        {
            logger.LogDebug(ex, "User memory promotion skipped for session {SessionId}", sessionId);
            return false;
        }
    }
}
