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

            var outcome = await AdmitOneWithOutcomeAsync(
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
            results.Add(new MemoryAdmissionResult(outcome.Status, proposal, outcome.AffectedScopes));
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
        CancellationToken cancellationToken = default) =>
        (await AdmitOneWithOutcomeAsync(
            memories,
            definition,
            sessionId,
            agentInstanceId,
            profile,
            entries,
            sourceEntryId,
            proposal,
            logger,
            cancellationToken).ConfigureAwait(false)).Status;

    private static async ValueTask<AdmissionOutcome> AdmitOneWithOutcomeAsync(
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
            return AdmissionOutcome.Unavailable;
        }

        if (!IsShapeValid(proposal))
        {
            return AdmissionOutcome.Rejected;
        }

        // A model's source classification is not owner approval. Keep all conversational
        // forgetting on the exact-item, exact-action memory.forget tool path.
        if (proposal.Operation == MemoryProposalOperation.Delete
            && MemoryProposalCodec.IsConversationalSource(proposal.Source))
            return new AdmissionOutcome(MemoryAdmissionStatus.ApprovalRequired, null);

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

        if (proposal.Operation == MemoryProposalOperation.Resolve)
        {
            var identity = proposal.ScopeHint is not MemoryScopeHint.Session
                && policy.IdentityUserRetrieval && promotedInstanceId != Guid.Empty && profile?.ProfileId is { } profileId
                && profileId != Guid.Empty ? new TrustedIdentityUserOwner(promotedInstanceId, profileId) : null;
            var user = proposal.ScopeHint is MemoryScopeHint.User && policy.UserRetrieval
                && profile is not null && profile.ProfileId != Guid.Empty ? new TrustedUserOwner(profile.ProfileId) : null;
            var resolution = await memories.ResolveOpenLoopsAsync(owner, identity, user, proposal.Subject, cancellationToken)
                .ConfigureAwait(false);
            if (resolution.Items.Count == 0) return new AdmissionOutcome(MemoryAdmissionStatus.NotFound, null);
            return new AdmissionOutcome(resolution.Changed ? MemoryAdmissionStatus.Resolved : MemoryAdmissionStatus.AlreadyResolved,
                resolution.Items.Select(item => item.Scope switch
                {
                    MemoryScope.Session => "session",
                    MemoryScope.IdentityUser => "identityUser",
                    _ => "user"
                }).Distinct().ToArray());
        }

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
                return AdmissionOutcome.Rejected;
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
                return AdmissionOutcome.Rejected;
            }
        }

        if (!allowIdentity && !allowUser)
        {
            var sessionOnly = SessionOnly(sessionOutcome);
            return new AdmissionOutcome(sessionOnly, UpsertScopes(sessionOutcome, identitySynced: false, userSynced: false));
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

        var status = ResolveUpsertStatus(
            sessionOutcome,
            identitySynced,
            userSynced,
            allowIdentity,
            allowUser);
        return new AdmissionOutcome(status, UpsertScopes(sessionOutcome, identitySynced, userSynced));
    }

    private static MemoryAdmissionStatus ResolveUpsertStatus(
        MemoryAdmissionStatus sessionOutcome,
        bool identitySynced,
        bool userSynced,
        bool allowIdentity,
        bool allowUser)
    {
        var promotionIncomplete =
            (allowIdentity && !identitySynced)
            || (allowUser && !userSynced);
        if (promotionIncomplete)
        {
            return identitySynced || userSynced
                ? sessionOutcome
                : SessionOnly(sessionOutcome);
        }

        return sessionOutcome;
    }

    private readonly record struct AdmissionOutcome(MemoryAdmissionStatus Status, IReadOnlyList<string>? AffectedScopes)
    {
        public static AdmissionOutcome Unavailable => new(MemoryAdmissionStatus.Unavailable, null);
        public static AdmissionOutcome Rejected => new(MemoryAdmissionStatus.Rejected, null);
    }

    private static IReadOnlyList<string>? UpsertScopes(
        MemoryAdmissionStatus sessionOutcome,
        bool identitySynced,
        bool userSynced)
    {
        if (!SessionLayerAffected(sessionOutcome))
        {
            return null;
        }

        var scopes = new List<string>(3) { "session" };
        if (identitySynced)
        {
            scopes.Add("identityUser");
        }

        if (userSynced)
        {
            scopes.Add("user");
        }

        return scopes;
    }

    private static bool SessionLayerAffected(MemoryAdmissionStatus sessionOutcome) =>
        sessionOutcome is MemoryAdmissionStatus.Stored
            or MemoryAdmissionStatus.Updated
            or MemoryAdmissionStatus.AlreadyStored;

    private static bool IsShapeValid(MemoryProposal proposal)
    {
        if (!Enum.IsDefined(proposal.Operation) || !Enum.IsDefined(proposal.Kind) || !Enum.IsDefined(proposal.Source)
            || proposal.ScopeHint is { } scope && !Enum.IsDefined(scope))
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

        if (proposal.Operation == MemoryProposalOperation.Resolve)
            return proposal.Kind == MemoryKind.OpenLoop;

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

    private static async ValueTask<AdmissionOutcome> DeleteAsync(
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
        var scopes = new List<string>(3);
        var deleted = false;
        var found = false;
        var rejected = false;
        var existing = await memories.FindActiveBySubjectAsync(owner, proposal.Kind, proposal.Subject, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            found = true;
            try
            {
                await memories.DeleteAsync(owner, existing.MemoryId, cancellationToken).ConfigureAwait(false);
                deleted = true;
                scopes.Add("session");
            }
            catch (AgentCoreException ex) when (ex.Code is "MemoryRejected" or "NotFound")
            {
                rejected = true;
                logger.LogDebug(ex, "Memory delete rejected for session {SessionId}", sessionId);
            }
        }

        if (allowIdentity && profile is not null)
        {
            var identityOwner = new TrustedIdentityUserOwner(instanceId, profile.ProfileId);
            var identity = await memories.FindActiveIdentityUserBySubjectAsync(
                identityOwner,
                proposal.Kind,
                proposal.Subject,
                cancellationToken).ConfigureAwait(false);
            if (identity is not null)
            {
                found = true;
                try
                {
                    await memories.DeleteIdentityUserAsync(
                        identityOwner,
                        identity.MemoryId,
                        retrievalAllowed: true,
                        cancellationToken).ConfigureAwait(false);
                    deleted = true;
                    scopes.Add("identityUser");
                }
                catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected" or "NotFound")
                {
                    rejected = true;
                    logger.LogDebug(ex, "Identity memory delete skipped for session {SessionId}", sessionId);
                }
            }
        }

        if (allowUser && profile is not null)
        {
            var userOwner = new TrustedUserOwner(profile.ProfileId);
            var match = await memories.FindActiveUserBySubjectAsync(
                userOwner,
                proposal.Kind,
                proposal.Subject,
                cancellationToken).ConfigureAwait(false);
            if (match is not null)
            {
                found = true;
                try
                {
                    await memories.DeleteUserAsync(
                        userOwner,
                        match.MemoryId,
                        retrievalAllowed: true,
                        cancellationToken).ConfigureAwait(false);
                    deleted = true;
                    scopes.Add("user");
                }
                catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected" or "NotFound")
                {
                    rejected = true;
                    logger.LogDebug(ex, "User memory delete skipped for session {SessionId}", sessionId);
                }
            }
        }

        _ = admission;
        return deleted
            ? new AdmissionOutcome(rejected ? MemoryAdmissionStatus.PartiallyDeleted : MemoryAdmissionStatus.Deleted, scopes)
            : new AdmissionOutcome(found ? MemoryAdmissionStatus.Rejected : MemoryAdmissionStatus.NotFound, null);
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
        var existing = await memories.FindActiveUserBySubjectAsync(
            userOwner,
            sessionItem.Kind,
            sessionItem.Subject,
            cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
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
            catch (AgentCoreException ex) when (ex.Code == "Conflict")
            {
                existing = await memories.FindActiveUserBySubjectAsync(
                    userOwner,
                    sessionItem.Kind,
                    sessionItem.Subject,
                    cancellationToken).ConfigureAwait(false);
                if (existing is null)
                {
                    logger.LogDebug(ex, "User memory promotion conflict without lookup for session {SessionId}", sessionId);
                    return false;
                }
            }
            catch (AgentCoreException ex) when (ex.Code is "PolicyDenied" or "MemoryRejected")
            {
                logger.LogDebug(ex, "User memory promotion skipped for session {SessionId}", sessionId);
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
            await memories.UpdateUserAsync(
                userOwner,
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
            logger.LogDebug(ex, "User memory update skipped for session {SessionId}", sessionId);
            return false;
        }
    }
}
