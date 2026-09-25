using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Admin;

public enum AdminLearnedMemoryScope
{
    Session,
    IdentityUser,
    User
}

public sealed record AdminLearnedMemoryItem(
    Guid MemoryId,
    MemoryKind Kind,
    string Subject,
    string Content,
    string ProvenanceSource,
    Guid? OriginSessionId,
    Guid? OriginMemoryId,
    DateTimeOffset RecordedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminLearnedMemoryListResult(
    AdminLearnedMemoryScope Scope,
    IReadOnlyList<AdminLearnedMemoryItem> Items);

public sealed record AdminLearnedMemoryResetResult(AdminLearnedMemoryScope Scope, int ItemsRemoved);

public sealed class AdminMemoryService(
    IAgentInstanceStore instances,
    IAgentDefinitionStore definitions,
    IMemoryStore sessions,
    IStructuredMemoryStore structuredStore,
    IStructuredMemoryService memories,
    ILocalUserProfileService localProfiles)
{
    public const int MaxListItems = MemoryLimits.MaxActiveItems;

    public async ValueTask<AdminLearnedMemoryListResult> ListAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var effective = await ResolveEffectiveMemoryPolicyAsync(instance, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);

        return scope switch
        {
            AdminLearnedMemoryScope.Session => await ListSessionAsync(instance, sessionId, profile.ProfileId, cancellationToken)
                .ConfigureAwait(false),
            AdminLearnedMemoryScope.IdentityUser => await ListIdentityUserAsync(
                    instance,
                    profile.ProfileId,
                    effective.IdentityUserRetrieval,
                    cancellationToken)
                .ConfigureAwait(false),
            AdminLearnedMemoryScope.User => await ListUserAsync(profile.ProfileId, effective.UserRetrieval, cancellationToken)
                .ConfigureAwait(false),
            _ => throw AgentCoreErrors.Validation("scope is invalid.")
        };
    }

    public async ValueTask EnsureDeleteAllowedAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var effective = await ResolveEffectiveMemoryPolicyAsync(instance, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        switch (scope)
        {
            case AdminLearnedMemoryScope.Session:
            {
                var session = await RequireSessionForInstanceAsync(sessionId, instance.InstanceId, profile.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                RequireSessionMemoryEnabled(session);
                return;
            }
            case AdminLearnedMemoryScope.IdentityUser:
                RequireIdentityRetrieval(effective);
                return;
            case AdminLearnedMemoryScope.User:
                RequireUserRetrieval(effective);
                return;
            default:
                throw AgentCoreErrors.Validation("scope is invalid.");
        }
    }

    public async ValueTask EnsureResetAllowedAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        CancellationToken cancellationToken = default) =>
        await EnsureDeleteAllowedAsync(instanceId, scope, sessionId, cancellationToken).ConfigureAwait(false);

    public async ValueTask DeleteAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid memoryId,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        await EnsureDeleteAllowedAsync(instanceId, scope, sessionId, cancellationToken).ConfigureAwait(false);
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var effective = await ResolveEffectiveMemoryPolicyAsync(instance, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);

        switch (scope)
        {
            case AdminLearnedMemoryScope.Session:
            {
                var session = await RequireSessionForInstanceAsync(sessionId, instance.InstanceId, profile.ProfileId, cancellationToken)
                    .ConfigureAwait(false);
                RequireSessionMemoryEnabled(session);
                await memories.DeleteAsync(new TrustedMemoryOwner(session.SessionId), memoryId, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            case AdminLearnedMemoryScope.IdentityUser:
            {
                RequireIdentityRetrieval(effective);
                var owner = new TrustedIdentityUserOwner(instance.InstanceId, profile.ProfileId);
                await memories.DeleteIdentityUserAsync(owner, memoryId, effective.IdentityUserRetrieval, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            case AdminLearnedMemoryScope.User:
            {
                RequireUserRetrieval(effective);
                await memories.DeleteUserAsync(
                        new TrustedUserOwner(profile.ProfileId),
                        memoryId,
                        effective.UserRetrieval,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            default:
                throw AgentCoreErrors.Validation("scope is invalid.");
        }
    }

    public async ValueTask<AdminLearnedMemoryResetResult> ResetScopeAsync(
        Guid instanceId,
        AdminLearnedMemoryScope scope,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var instance = await RequireManagedInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var effective = await ResolveEffectiveMemoryPolicyAsync(instance, cancellationToken).ConfigureAwait(false);
        var profile = await localProfiles.GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);

        var removed = scope switch
        {
            AdminLearnedMemoryScope.Session => await ResetSessionAsync(
                    instance,
                    sessionId,
                    profile.ProfileId,
                    cancellationToken)
                .ConfigureAwait(false),
            AdminLearnedMemoryScope.IdentityUser => await memories.ResetIdentityUserScopeAsync(
                    new TrustedIdentityUserOwner(instance.InstanceId, profile.ProfileId),
                    effective.IdentityUserRetrieval,
                    cancellationToken)
                .ConfigureAwait(false),
            AdminLearnedMemoryScope.User => await memories.ResetUserScopeAsync(
                    new TrustedUserOwner(profile.ProfileId),
                    effective.UserRetrieval,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => throw AgentCoreErrors.Validation("scope is invalid.")
        };

        return new AdminLearnedMemoryResetResult(scope, removed);
    }

    private async ValueTask<AdminLearnedMemoryListResult> ListSessionAsync(
        AgentInstance instance,
        Guid? sessionId,
        Guid localProfileId,
        CancellationToken cancellationToken)
    {
        var session = await RequireSessionForInstanceAsync(sessionId, instance.InstanceId, localProfileId, cancellationToken)
            .ConfigureAwait(false);
        RequireSessionMemoryEnabled(session);
        var items = new List<AdminLearnedMemoryItem>();
        foreach (var item in await structuredStore.ListActiveAsync(session.SessionId, cancellationToken).ConfigureAwait(false))
        {
            if (item.Scope != MemoryScope.Session)
            {
                continue;
            }

            items.Add(Map(item));
            if (items.Count >= MaxListItems)
            {
                break;
            }
        }

        return new AdminLearnedMemoryListResult(AdminLearnedMemoryScope.Session, items);
    }

    private async ValueTask<AdminLearnedMemoryListResult> ListIdentityUserAsync(
        AgentInstance instance,
        Guid profileId,
        bool retrievalAllowed,
        CancellationToken cancellationToken)
    {
        if (!retrievalAllowed)
        {
            return new AdminLearnedMemoryListResult(AdminLearnedMemoryScope.IdentityUser, []);
        }

        var items = new List<AdminLearnedMemoryItem>();
        foreach (var item in await structuredStore
                     .ListActiveIdentityUserAsync(instance.InstanceId, profileId, cancellationToken)
                     .ConfigureAwait(false))
        {
            if (item.Scope != MemoryScope.IdentityUser
                || item.OwnerInstanceId != instance.InstanceId
                || item.OwnerProfileId != profileId)
            {
                continue;
            }

            items.Add(Map(item));
            if (items.Count >= MaxListItems)
            {
                break;
            }
        }

        return new AdminLearnedMemoryListResult(AdminLearnedMemoryScope.IdentityUser, items);
    }

    private async ValueTask<AdminLearnedMemoryListResult> ListUserAsync(
        Guid profileId,
        bool retrievalAllowed,
        CancellationToken cancellationToken)
    {
        if (!retrievalAllowed)
        {
            return new AdminLearnedMemoryListResult(AdminLearnedMemoryScope.User, []);
        }

        var items = new List<AdminLearnedMemoryItem>();
        foreach (var item in await structuredStore.ListActiveUserAsync(profileId, cancellationToken).ConfigureAwait(false))
        {
            if (item.Scope != MemoryScope.User || item.OwnerProfileId != profileId)
            {
                continue;
            }

            items.Add(Map(item));
            if (items.Count >= MaxListItems)
            {
                break;
            }
        }

        return new AdminLearnedMemoryListResult(AdminLearnedMemoryScope.User, items);
    }

    private async ValueTask<int> ResetSessionAsync(
        AgentInstance instance,
        Guid? sessionId,
        Guid localProfileId,
        CancellationToken cancellationToken)
    {
        var session = await RequireSessionForInstanceAsync(sessionId, instance.InstanceId, localProfileId, cancellationToken)
            .ConfigureAwait(false);
        RequireSessionMemoryEnabled(session);
        return await memories.ResetSessionScopeAsync(new TrustedMemoryOwner(session.SessionId), cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<AgentInstance> RequireManagedInstanceAsync(Guid instanceId, CancellationToken cancellationToken)
    {
        var instance = await instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (instance.Compatibility)
        {
            throw AgentCoreErrors.Forbidden("Memory administration requires a managed instance.");
        }

        return instance;
    }

    private async ValueTask<MemoryPolicy> ResolveEffectiveMemoryPolicyAsync(
        AgentInstance instance,
        CancellationToken cancellationToken)
    {
        var definition = await definitions
            .GetAsync(instance.DefinitionId, instance.ActiveVersion, cancellationToken)
            .ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound(
                $"Agent '{instance.DefinitionId}' version {instance.ActiveVersion} was not found.");
        return definition.MemoryPolicy ?? MemoryPolicy.Disabled;
    }

    private async ValueTask<SessionSnapshot> RequireSessionForInstanceAsync(
        Guid? sessionId,
        Guid instanceId,
        Guid localProfileId,
        CancellationToken cancellationToken)
    {
        if (sessionId is not Guid resolved || resolved == Guid.Empty)
        {
            throw AgentCoreErrors.Validation("sessionId is required for Session scope.");
        }

        var snapshot = await sessions.LoadMetadataAsync(resolved, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Session was not found.");
        if (snapshot.AgentInstanceId != instanceId)
        {
            throw AgentCoreErrors.NotFound("Session was not found for this instance.");
        }

        if (snapshot.ProfileId != localProfileId)
        {
            throw AgentCoreErrors.Forbidden("Session does not belong to the trusted local profile.");
        }

        return snapshot;
    }

    private static void RequireSessionMemoryEnabled(SessionSnapshot snapshot)
    {
        if (snapshot.Definition.MemoryPolicy?.SessionMemory != true)
        {
            throw new AgentCoreException("PolicyDenied", "Session learned memory is not enabled for this session.", 403);
        }
    }

    private static void RequireIdentityRetrieval(MemoryPolicy policy)
    {
        if (!policy.IdentityUserRetrieval)
        {
            throw new AgentCoreException("PolicyDenied", "IdentityUser learned memory retrieval is not enabled.", 403);
        }
    }

    private static void RequireUserRetrieval(MemoryPolicy policy)
    {
        if (!policy.UserRetrieval)
        {
            throw new AgentCoreException("PolicyDenied", "User-wide learned memory retrieval is not enabled.", 403);
        }
    }

    private static AdminLearnedMemoryItem Map(StructuredMemoryItem item) =>
        new(
            item.MemoryId,
            item.Kind,
            item.Subject,
            item.Content,
            item.Provenance.Source,
            item.Provenance.OriginSessionId,
            item.Provenance.OriginMemoryId,
            item.Provenance.RecordedAt,
            item.UpdatedAt);
}
