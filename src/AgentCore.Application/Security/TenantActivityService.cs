using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantActivitySession(TenantSessionMetadata Session, SessionOriginKind Origin, IReadOnlyList<string> Surfaces,
    Guid? InitialRunId, bool CanContinue, long SecurityRevision);
public sealed record TenantActivityPage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, long SecurityRevision);
public sealed record TenantActivityRun(Guid RunId, Guid SessionId, Guid AgentInstanceId, TrustedExecutionContext Context,
    AgentRunStatus Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, ActivationKind ActivationKind,
    AgentRunOutputContract OutputContract, string ModelCatalogKey, int AttemptCount, int MaxAttempts, string? ProgressSummary, string? Objective, string? OutcomeText, AgentRunOutcomeKind? OutcomeKind,
    string? FailureCode, string? FailureSummary, Guid? DiagnosticId, bool CancellationRequested, bool CanCancel);
public sealed record TenantActivityDelivery(string Status, Guid? TargetSessionId, string? Reason);
public sealed record TenantBackgroundDetail(TenantActivitySession Session, TenantActivityRun? InitialRun,
    string? InitialTitle, TenantActivityDelivery? CompletionDelivery);

// Original-human Activity inspection is independent from communication and executor authority.
public sealed class TenantActivityService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    ITenantRunStore runs, IdentityService identity, IamAuthorizer authorizer)
{
    public async ValueTask<TenantActivityPage<TenantActivitySession>> ListSessionsAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int page = 0, int pageSize = 30, bool backgroundOnly = false, CancellationToken ct = default)
    {
        var (state, agent) = await Agent(tenant, instance, human, ct).ConfigureAwait(false);
        var allowed = await Readable(state, human.PrincipalId, "sessions.read", TenantSessionRules.TryId, ct).ConfigureAwait(false);
        var saved = await sessions.ListTenantSessionsAsync(tenant, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct,
            includeArchived: true, surface: backgroundOnly ? SessionSurface.BackgroundWork : null, instanceId: instance, recentFirst: true).ConfigureAwait(false);
        var items = new List<TenantActivitySession>();
        foreach (var session in saved.Items) items.Add(await Session(state, agent, session, human, ct).ConfigureAwait(false));
        await Current(state, human, ct).ConfigureAwait(false);
        return new(items, saved.Total, saved.Page, saved.PageSize, state.Tenant.Revision);
    }
    public async ValueTask<TenantActivityPage<TenantBackgroundDetail>> ListBackgroundAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        var saved = await ListSessionsAsync(tenant, instance, human, page, pageSize, backgroundOnly: true, ct).ConfigureAwait(false);
        var items = new List<TenantBackgroundDetail>();
        foreach (var row in saved.Items) items.Add(await ReadBackgroundAsync(tenant, instance, row.Session.Owner.SessionId, human, ct).ConfigureAwait(false));
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != saved.SecurityRevision) throw new SecurityConflictException("stale_revision");
        return new(items, saved.Total, saved.Page, saved.PageSize, saved.SecurityRevision);
    }
    public async ValueTask<TenantActivityPage<TenantActivityRun>> ListAgentRunsAsync(Guid tenant, Guid instance, TenantSessionInitiator human,
        int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        var (state, _) = await Agent(tenant, instance, human, ct).ConfigureAwait(false);
        var allowedSessions = await Readable(state, human.PrincipalId, "sessions.read", TenantSessionRules.TryId, ct).ConfigureAwait(false);
        var allowedRuns = await Readable(state, human.PrincipalId, "agent-runs.read", TenantRunRules.TryId, ct).ConfigureAwait(false);
        var saved = await runs.ListTenantSessionRunsAsync(tenant, null, allowedRuns, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct, instance, allowedSessions).ConfigureAwait(false);
        var items = new List<TenantActivityRun>(); foreach (var run in saved.Items) items.Add(await Run(state, run, human, ct).ConfigureAwait(false));
        await Current(state, human, ct).ConfigureAwait(false); return new(items, saved.Total, saved.Page, saved.PageSize, state.Tenant.Revision);
    }
    public async ValueTask<TenantActivitySession> ReadSessionAsync(Guid tenant, Guid instance, Guid sessionId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var (state, agent, session) = await OwnedSession(tenant, instance, sessionId, human, ct).ConfigureAwait(false);
        var result = await Session(state, agent, session, human, ct).ConfigureAwait(false);
        await Current(state, human, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<TenantActivityPage<TenantActivityRun>> ListRunsAsync(Guid tenant, Guid instance, Guid sessionId, TenantSessionInitiator human,
        int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        var (state, _, _) = await OwnedSession(tenant, instance, sessionId, human, ct).ConfigureAwait(false);
        var allowed = await Readable(state, human.PrincipalId, "agent-runs.read", TenantRunRules.TryId, ct).ConfigureAwait(false);
        var saved = await runs.ListTenantSessionRunsAsync(tenant, sessionId, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
        var items = new List<TenantActivityRun>(); foreach (var run in saved.Items) items.Add(await Run(state, run, human, ct).ConfigureAwait(false));
        await Current(state, human, ct).ConfigureAwait(false); return new(items, saved.Total, saved.Page, saved.PageSize, state.Tenant.Revision);
    }
    public async ValueTask<TenantActivityRun> ReadAgentRunAsync(Guid tenant, Guid instance, Guid runId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        _ = await Agent(tenant, instance, human, ct).ConfigureAwait(false);
        var run = await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (run.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
        return await ReadRunAsync(tenant, instance, run.SessionId, runId, human, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantActivityRun> ReadRunAsync(Guid tenant, Guid instance, Guid sessionId, Guid runId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var (state, _, session) = await OwnedSession(tenant, instance, sessionId, human, ct).ConfigureAwait(false);
        var run = await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (run.SessionId != sessionId || run.AgentInstanceId != instance || run.Owner.ExecutorPrincipalId != session.Owner.ExecutorPrincipalId)
            throw new AccessDeniedException("resource_unavailable");
        await Require(state, human.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false);
        var result = await Run(state, run, human, ct).ConfigureAwait(false); await Current(state, human, ct).ConfigureAwait(false); return result;
    }
    public async ValueTask<TenantBackgroundDetail> ReadBackgroundAsync(Guid tenant, Guid instance, Guid sessionId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var (state, agent, session) = await OwnedSession(tenant, instance, sessionId, human, ct).ConfigureAwait(false);
        var origin = session.Snapshot.Origin;
        if (!session.Snapshot.Surfaces.HasFlag(SessionSurface.BackgroundWork) || origin.InitialBackgroundAgentRunId is not { } initialId)
            throw new AccessDeniedException("resource_unavailable");
        TenantActivityRun? initial = null; TenantActivityDelivery? delivery = null;
        if (await Can(state, human.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, initialId), ct).ConfigureAwait(false))
        {
            var run = await runs.FindTenantRunAsync(tenant, initialId, ct).ConfigureAwait(false);
            if (run is not null && run.SessionId == sessionId && run.AgentInstanceId == instance && run.Owner.ExecutorPrincipalId == session.Owner.ExecutorPrincipalId)
            {
                var seen = new HashSet<Guid>();
                while (run.Admission.OutputContract == AgentRunOutputContract.BackgroundOutcome && run.Result is { OutcomeKind: AgentRunOutcomeKind.Continued, ContinuationRunId: { } successor }
                    && seen.Add(run.AgentRunId) && seen.Count < 64
                    && await Can(state, human.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, successor), ct).ConfigureAwait(false))
                {
                    var next = await runs.FindTenantRunAsync(tenant, successor, ct).ConfigureAwait(false);
                    if (next is null) break;
                    TenantBackgroundLineage.RequireHandoff(run, next); run = next;
                }
                initial = await Run(state, run, human, ct).ConfigureAwait(false);
                var receipt = run.Result?.OutcomeKind == AgentRunOutcomeKind.Continued ? null : await runs.FindTenantCompletionAsync(run.Owner, run.AgentRunId, ct).ConfigureAwait(false);
                Guid? target = receipt?.ParentSessionId is { } parent && await Can(state, human.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, parent), ct).ConfigureAwait(false) ? parent : null;
                delivery = run.Result?.OutcomeKind == AgentRunOutcomeKind.Continued ? null : new(receipt?.Status == CompletionInboxStatus.DeliveryQueued ? "admitted" : receipt?.Status.ToString() ?? (origin.ReportCompletionToOrigin ? "Pending" : "NotRequested"), target, receipt?.SkipReason);
            }
        }
        var result = new TenantBackgroundDetail(await Session(state, agent, session, human, ct).ConfigureAwait(false), initial, origin.InitialTitle, delivery);
        await Current(state, human, ct).ConfigureAwait(false); return result;
    }
    private async ValueTask<(TenantSecurityState State, TenantAgent Agent)> Agent(Guid tenant, Guid instance, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, human.PrincipalId, "agents.read", AccessScope.ForResource(tenant, "agent", instance.ToString("D")), ct).ConfigureAwait(false);
        return (state, agent);
    }
    private async ValueTask<(TenantSecurityState State, TenantAgent Agent, TenantSession Session)> OwnedSession(Guid tenant, Guid instance, Guid sessionId, TenantSessionInitiator human, CancellationToken ct)
    {
        var (state, agent) = await Agent(tenant, instance, human, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        if (session.Owner.AgentInstanceId != instance || session.Owner.ExecutorPrincipalId != agent.PrincipalId) throw new AccessDeniedException("resource_unavailable");
        await Require(state, human.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
        return (state, agent, session);
    }
    private async ValueTask<TenantActivitySession> Session(TenantSecurityState state, TenantAgent agent, TenantSession saved, TenantSessionInitiator human, CancellationToken ct)
    {
        var s = saved.Snapshot; var tenant = saved.Owner.TenantId;
        var manage = await Can(state, human.PrincipalId, "sessions.manage", TenantSessionRules.Scope(tenant, saved.Owner.SessionId), ct).ConfigureAwait(false);
        var canContinue = (manage || s.Surfaces.HasFlag(SessionSurface.ChatList)) && agent.Instance.Lifecycle == AgentInstanceLifecycle.Active && s.ArchivedAt is null
            && s.Surfaces.HasFlag(SessionSurface.BackgroundWork) && !SessionLifecycle.IsTerminal(s.LifecycleStatus) && s.Status is not (SessionStatus.Ended or SessionStatus.Ending)
            && state.SystemIdentities.Any(i => i.PrincipalId == agent.PrincipalId && i.Status == IdentityStatus.Active)
            && state.Principals.Any(p => p.PrincipalId == agent.PrincipalId && p.Status == IdentityStatus.Active)
            && await Can(state, human.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", agent.Instance.InstanceId.ToString("D")), ct).ConfigureAwait(false);
        var persona = s.PinnedPersona ?? s.Definition.Identity;
        var metadata = new TenantSessionMetadata(saved.Owner, s.Title, s.Mode, s.Status, s.Revision, s.CreatedAt, s.UpdatedAt, s.LifecycleStatus, s.ArchivedAt,
            s.Definition.Id, s.Definition.Version, persona.Name, persona.Role, manage);
        var initial = s.Origin.InitialBackgroundAgentRunId is { } id && await Can(state, human.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, id), ct).ConfigureAwait(false) ? id : (Guid?)null;
        return new(metadata, s.Origin.Kind, new[] { SessionSurface.ChatList, SessionSurface.BackgroundWork }.Where(f => s.Surfaces.HasFlag(f)).Select(f => f.ToString()).ToArray(), initial, canContinue, state.Tenant.Revision);
    }
    private async ValueTask<TenantActivityRun> Run(TenantSecurityState state, AgentRun run, TenantSessionInitiator human, CancellationToken ct) =>
        new(run.AgentRunId, run.SessionId, run.AgentInstanceId, run.Admission.ExecutionContext!, run.Status, run.Revision, run.CreatedAtUtc, run.UpdatedAtUtc,
            run.Admission.Activation.Kind, run.Admission.OutputContract, run.PinnedModel.CatalogKey, run.AttemptCount, run.MaxAttempts, run.Progress?.Summary, TenantExperienceReviewRules.DisplayObjective(run), run.Result?.Text, run.Result?.OutcomeKind,
            run.Failure?.Code, run.Failure?.Summary, run.Failure?.DiagnosticId, run.CancellationRequested, !run.IsTerminal && !run.CancellationRequested && await Can(state, human.PrincipalId, "agent-runs.cancel", TenantRunRules.Scope(state.Tenant.TenantId, run.AgentRunId), ct).ConfigureAwait(false)
                && await Can(state, human.PrincipalId, "agents.chat", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false));
    private delegate bool ResourceId(AccessScope scope, out Guid id);
    private async ValueTask<IReadOnlyList<Guid>?> Readable(TenantSecurityState state, Guid actor, string permission, ResourceId parse, CancellationToken ct)
    {
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (decision.Allowed) return null;
        if (decision.ReasonCode != "permission_denied") throw new AccessDeniedException(decision.ReasonCode);
        var ids = new HashSet<Guid>();
        foreach (var assignment in state.Assignments.Where(a => a.PrincipalId == actor))
            if (parse(assignment.Scope, out var id) && await Can(state, actor, permission, assignment.Scope, ct).ConfigureAwait(false)) ids.Add(id);
        return ids.ToArray();
    }
    private async ValueTask<bool> Can(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct) =>
        (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed;
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { if (!await Can(state, actor, permission, scope, ct).ConfigureAwait(false)) throw new AccessDeniedException("permission_denied"); }
    private async ValueTask Current(TenantSecurityState state, TenantSessionInitiator human, CancellationToken ct)
    { if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, state.Tenant.TenantId, human, ct).ConfigureAwait(false)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
}
