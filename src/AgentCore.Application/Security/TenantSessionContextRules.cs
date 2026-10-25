using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

public static class TenantSessionContextRules
{
    public static void Shape(TenantContextSwitch request, TenantSession saved, TenantAgent agent, TenantSecurityState state, bool live)
    {
        var selection = saved.Snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"); selection.Validate();
        if ((request.Human is null) == (request.Agent is null) || request.Agent is null && request.Tool is not null || request.Agent is not null && !live) throw new AccessDeniedException("context_actor_unavailable");
        if (request.TransitionId == Guid.Empty || request.SpaceId == Guid.Empty || request.EnvironmentId == Guid.Empty || request.Owner != saved.Owner
            || request.Owner.TenantId != state.Tenant.TenantId || agent.TenantId != request.Owner.TenantId || agent.Instance.InstanceId != saved.Owner.AgentInstanceId
            || agent.PrincipalId != saved.Owner.ExecutorPrincipalId || selection.TenantId != saved.Owner.TenantId) throw new AccessDeniedException("tenant_context_mismatch");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        if (saved.Snapshot.DurablyDeletedAt is not null || saved.Snapshot.ArchivedAt is not null || saved.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || saved.Snapshot.LifecycleStatus is SessionLifecycleStatus.Completed or SessionLifecycleStatus.Cancelled) throw new SecurityConflictException("session_read_only");
        if (state.Tenant.Revision != request.ExpectedSecurityRevision || agent.Instance.Revision != request.ExpectedInstanceRevision
            || selection.Revision != request.ExpectedSelectionRevision || !live && saved.Snapshot.Revision != request.ExpectedSessionRevision)
            throw new SecurityConflictException("stale_revision");
    }
    public static SessionContextSelection Selection(TenantContextSwitch request, SessionContextSelection current, Guid? barrier, DateTimeOffset at)
    {
        var value = new SessionContextSelection(request.Owner.TenantId, request.SpaceId, request.EnvironmentId,
            request.EnvironmentId is null ? 0 : request.EnvironmentSelectionEpoch, checked(current.Revision + 1), at, barrier, request.TransitionId);
        value.Validate(); return value;
    }
    public static bool IsReplay(TenantContextSwitch request, SessionContextSelection? selection) => selection is not null
        && selection.TransitionId == request.TransitionId && selection.SpaceId == request.SpaceId && selection.EnvironmentId == request.EnvironmentId
        && selection.EnvironmentSelectionEpoch == (request.EnvironmentId is null ? 0 : request.EnvironmentSelectionEpoch)
        && selection.Revision == request.ExpectedSelectionRevision + 1;
    public static async ValueTask RequireAsync(TenantContextSwitch request, TenantSession saved, TenantAgent agent, TenantSecurityState state,
        IamAuthorizer iam, Space destination, SpaceMembership? membership, AgentCore.Domain.Spaces.Environment? environment, bool live, CancellationToken ct)
    {
        Shape(request, saved, agent, state, live);
        if (destination.TenantId != request.Owner.TenantId || destination.SpaceId != request.SpaceId || destination.DeletedAt is not null
            || membership is null || membership.TenantId != destination.TenantId || membership.SpaceId != destination.SpaceId || membership.InstanceId != agent.Instance.InstanceId
            || destination.Kind == SpaceKind.Private && destination.OwnerInstanceId != agent.Instance.InstanceId) throw new AccessDeniedException("space_membership_unavailable");
        if (request.EnvironmentId is { } id && (!destination.EnvironmentsEnabled || environment is null || environment.EnvironmentId != id
            || environment.SpaceId != destination.SpaceId || environment.TenantId != destination.TenantId || environment.DeletedAt is not null
            || request.EnvironmentSelectionEpoch != destination.EnvironmentSelectionEpoch)) throw new AccessDeniedException("environment_unavailable");
        foreach (var key in new[] { "sessions.read", "sessions.manage" })
            await Require(iam, state, request.ActorPrincipalId, key, TenantSessionRules.Scope(state.Tenant.TenantId, request.Owner.SessionId), ct).ConfigureAwait(false);
        var scope = TenantSpaceService.Scope(state.Tenant.TenantId, destination.SpaceId);
        foreach (var principal in new[] { request.ActorPrincipalId, agent.PrincipalId })
        {
            await Require(iam, state, principal, "spaces.read", scope, ct).ConfigureAwait(false);
            if (request.EnvironmentId is not null)
                foreach (var key in new[] { "environments.read", "environments.activate" }) await Require(iam, state, principal, key, scope, ct).ConfigureAwait(false);
        }
    }
    public static async ValueTask RequireAgentAsync(TenantContextSwitch request, TenantSession saved, TenantAgent agent, AgentRun run,
        AgentDefinition baseline, TenantSecurityState state, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct)
    {
        var frame = request.Agent ?? throw new AccessDeniedException("context_actor_unavailable");
        TenantRuntimeRules.Require(frame, saved.Owner, run, at);
        if (frame.SessionId != request.Owner.SessionId || frame.RunId != run.AgentRunId
            || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.ExecutionContext is not { OriginalInitiatorPrincipalId: { } human, Origin: ExecutionOrigin.UserMessage or ExecutionOrigin.ApprovalContinuation }
            || request.Tool is not { } tool || !AgentCore.Application.Tools.TenantContextToolContract.IsSwitch(tool)
            || !AgentCore.Application.Tools.TenantContextToolContract.Allows(run.Admission.Configuration!.Definition, tool)
            || !AgentCore.Application.Tools.TenantContextToolContract.Allows(InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), tool)
            || tool != "spaces.activate" && request.SpaceId != run.Admission.Configuration.SpaceContext!.SpaceId
            || tool == "environments.activate" && request.EnvironmentId is null
            || tool == "environments.deactivate" && request.EnvironmentId is not null)
            throw new AccessDeniedException("context_actor_unavailable");
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(state.Tenant.TenantId, saved.Owner.SessionId)),
            ("sessions.manage", TenantSessionRules.Scope(state.Tenant.TenantId, saved.Owner.SessionId)),
            ("agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", agent.Instance.InstanceId.ToString("D"))),
            ("spaces.read", TenantSpaceService.Scope(state.Tenant.TenantId, request.SpaceId)) }
            .Concat(request.EnvironmentId is not null || tool.StartsWith("environments.", StringComparison.Ordinal)
                ? new[] { ("environments.read", TenantSpaceService.Scope(state.Tenant.TenantId, request.SpaceId)), ("environments.activate", TenantSpaceService.Scope(state.Tenant.TenantId, request.SpaceId)) } : []))
        {
            foreach (var actor in new[] { human, agent.PrincipalId }) await Require(iam, state, actor, key, scope, ct).ConfigureAwait(false);
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).Mode == OperationApprovalMode.Always)
                throw new AccessDeniedException("context_review_required");
        }
    }
    private static async ValueTask Require(IamAuthorizer iam, TenantSecurityState state, Guid principal, string key, AccessScope scope, CancellationToken ct)
    { var result = await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false); if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode); }
}
