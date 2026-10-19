using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantHumanInputRules
{
    public static async ValueTask RequireAsync(TenantHumanInput input, TenantSession session, TenantAgent agent,
        TenantSecurityState state, IamAuthorizer iam, bool live, CancellationToken ct)
    {
        if (input.Owner != session.Owner || input.Owner.TenantId != state.Tenant.TenantId || input.Admission.Run.SessionId != input.Owner.SessionId
            || agent.TenantId != input.Owner.TenantId || agent.PrincipalId != input.Owner.ExecutorPrincipalId || agent.Instance.InstanceId != input.Owner.AgentInstanceId)
            throw new AccessDeniedException("resource_unavailable");
        foreach (var (key, scope) in new[] { ("sessions.read", TenantSessionRules.Scope(input.Owner.TenantId, input.Owner.SessionId)),
            ("agents.chat", AccessScope.ForResource(input.Owner.TenantId, "agent", input.Owner.AgentInstanceId.ToString("D"))) })
            if (!(await iam.CheckTenantAsync(state, input.Human.PrincipalId, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
        TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        if (state.Tenant.Revision != input.SecurityRevision || agent.Instance.Revision != input.Admission.ExpectedInstanceRevision
            || (live ? session.Snapshot.Revision < input.Admission.ExpectedSessionRevision : session.Snapshot.Revision != input.Admission.ExpectedSessionRevision))
            throw new SecurityConflictException("stale_revision");
        Eligible(session.Snapshot);
    }
    public static void Eligible(SessionSnapshot snapshot)
    {
        if (!snapshot.Surfaces.HasFlag(SessionSurface.ChatList) || snapshot.DurablyDeletedAt is not null || snapshot.ArchivedAt is not null
            || SessionLifecycle.IsTerminal(snapshot.LifecycleStatus)
            || snapshot.Status is SessionStatus.Ended or SessionStatus.Ending) throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Session is unavailable for new input.");
        if (snapshot.LifecycleStatus != SessionLifecycleStatus.Active) throw AgentCore.Application.Sessions.AgentCoreErrors.Validation("Resume the Session before sending input.");
    }
    public static TenantRunReceipt Receipt(TenantHumanInputCommit commit)
    {
        var result = commit.Admission; var run = result.Run;
        return new(result.Created, run.AgentRunId, result.EntryId, run.ResponseId!.Value, run.ActivationId, run.Admission.Activation.RootActivationId,
            result.SessionRevision, commit.SecurityRevision);
    }
    public static async ValueTask<TenantHumanInputCommit> CommitQuiescentAsync(TenantHumanInput input, IIamStore security,
        ITenantRunStore runs, ITenantSessionStore sessions, IIdGenerator ids, TimeProvider time, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(input.Owner.TenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (state.Tenant.Revision != input.SecurityRevision) throw new SecurityConflictException("stale_revision");
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), input.Owner.TenantId, input.Human.PrincipalId, "agent-runs.admit", input.Admission.Run.AgentRunId.ToString("D"), "allowed", "human_message_admitted", time.GetUtcNow());
        var result = await runs.TryAdmitTenantHumanRunAsync(next, state.Tenant.Revision, input.Admission, input.Human, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        var saved = await sessions.FindTenantSessionAsync(input.Owner.TenantId, input.Owner.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return new(result, saved.Snapshot, result.Created ? next.Tenant.Revision : state.Tenant.Revision);
    }
}
