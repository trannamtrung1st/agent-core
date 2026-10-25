using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSessionRules
{
    public static void RequireExecutor(TenantSecurityState state, TenantAgent agent)
    {
        if (!state.SystemIdentities.Any(i => i.AgentInstanceId == agent.Instance.InstanceId && i.PrincipalId == agent.PrincipalId && i.Status == IdentityStatus.Active)
            || !state.Principals.Any(p => p.PrincipalId == agent.PrincipalId && p.Kind == PrincipalKind.SystemIdentity && p.Status == IdentityStatus.Active))
            throw new AccessDeniedException("executor_unavailable");
    }
    public static AccessScope Scope(Guid tenant, Guid session) => AccessScope.ForResource(tenant, "session", session.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "session"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static void ValidateModelChange(Guid tenant, TenantSessionChange change)
    {
        if (change.Action != TenantSessionAction.SelectModel) return;
        if (change.Model is not { Binding: { } binding } model || binding.Route.TenantId != tenant
            || model.SelectionSource is not (ModelSelectionSource.User or ModelSelectionSource.AgentDefault or ModelSelectionSource.TenantDefault))
            throw new AccessDeniedException("model_context_required");
        binding.RequireIdentity(model.OfferingId, model.ConnectionId, model.ExternalModelId);
    }
    public static void ValidateCreation(TenantSessionRoot owner, SessionSnapshot snapshot, TenantAgent agent, TenantSessionInitiator initiator, AuthorizationAudit audit, AgentCore.Domain.Spaces.Space privateSpace)
    {
        snapshot.ContextSelection?.Validate();
        if (snapshot.ContextSelection is not { EnvironmentId: null, Revision: 1, EffectiveAfterRunId: null, TransitionId: null } selection
            || selection.TenantId != owner.TenantId || selection.SpaceId != privateSpace.SpaceId || selection.EnvironmentSelectionEpoch != 0
            || privateSpace.TenantId != owner.TenantId || privateSpace.OwnerInstanceId != owner.AgentInstanceId || privateSpace.Kind != AgentCore.Domain.Spaces.SpaceKind.Private
            || privateSpace.DeletedAt is not null || selection.UpdatedAt != snapshot.CreatedAt
            || owner.TenantId != agent.TenantId || owner.SessionId == Guid.Empty || owner.SessionId != snapshot.SessionId
            || owner.AgentInstanceId != agent.Instance.InstanceId || owner.AgentInstanceId != snapshot.AgentInstanceId
            || owner.ExecutorPrincipalId != agent.PrincipalId || owner.CreatedByPrincipalId != initiator.PrincipalId || audit.ActorPrincipalId != initiator.PrincipalId
            || audit.TenantId != owner.TenantId || owner.CreatedAt != snapshot.CreatedAt || snapshot.UpdatedAt != snapshot.CreatedAt
            || snapshot.Revision != 1 || snapshot.Status != SessionStatus.Created || snapshot.Mode is not (SessionMode.Text or SessionMode.Voice)
            || snapshot.PendingMode is not null || snapshot.ProfileId is not null || snapshot.Entries.Count != 0 || snapshot.PendingAgentInputIds.Count != 0
            || snapshot.Summary != "" || snapshot.SummarizedThroughEntrySequence != 0 || snapshot.PendingTopic is not null || snapshot.RuntimeEpoch != 0
            || snapshot.ArchivedAt is not null || snapshot.DurablyDeletedAt is not null || snapshot.LastEntrySequence != 0
            || snapshot.Definition.Id != agent.Instance.DefinitionId || snapshot.Definition.Version != agent.Instance.ActiveVersion
            || snapshot.PinnedPersona != agent.Instance.Persona || snapshot.PinnedPersonaRevision != agent.Instance.PersonaRevision
            || snapshot.LifecycleStatus != SessionLifecycleStatus.Active || snapshot.Origin != SessionOrigin.UserChat || snapshot.Surfaces != SessionSurface.ChatList)
            throw new ArgumentException("Invalid tenant Session admission.");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
    }
}
