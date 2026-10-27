using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Goals;

public sealed record SessionGoalsView(GoalPage<SessionGoal> Goals, Guid? FocusedGoalId, long FocusRevision, long SessionRevision, long SecurityRevision, bool CanManage, bool CanAccept, Guid? CurrentSpaceId);
public sealed class TenantSessionGoalService(IIamStore security, ITenantSessionStore sessions, ITenantSessionGoalStore goals,
    ITenantAgentStore agents, ITenantSpaceStore spaces, IdentityService identity, IamAuthorizer iam, IIdGenerator ids, TimeProvider time,
    TenantRunService runService, ITenantSessionGoalControl? control = null)
{
    public async ValueTask<SessionGoalsView> ListAsync(Guid tenant, Guid sessionId, TenantSessionInitiator human, int page = 0, int pageSize = 20, CancellationToken ct = default)
    {
        var (state, session) = await ReadSession(tenant, sessionId, human, ct);
        await GoalAuthority.Require(state, human.PrincipalId, "goals.read", TenantSessionRules.Scope(tenant, sessionId), iam, ct);
        var all = await goals.ListGoalsAsync(tenant, sessionId, 0, GoalRules.MaxGoals, ct); var readable = new List<SessionGoal>();
        foreach (var goal in all.Items)
            try { await ReadAuthority(state, session, goal, new(human), ct); readable.Add(goal); }
            catch (AccessDeniedException) { }
        await Unchanged(tenant, human, state.Tenant.Revision, ct);
        var scope = TenantSessionRules.Scope(tenant, sessionId); page = Math.Clamp(page, 0, 100_000); pageSize = Math.Clamp(pageSize, 1, 100);
        return new(new(readable.Skip(page * pageSize).Take(pageSize).ToArray(), readable.Count, page, pageSize), readable.Any(g => g.GoalId == session.Snapshot.FocusedGoalId) ? session.Snapshot.FocusedGoalId : null,
            session.Snapshot.GoalFocusRevision, session.Snapshot.Revision, state.Tenant.Revision,
            (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("goals.manage"), scope, ct)).Allowed,
            (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("goals.accept"), scope, ct)).Allowed, session.Snapshot.ContextSelection?.SpaceId);
    }
    public async ValueTask<SessionGoal> ReadAsync(Guid tenant, Guid sessionId, Guid goalId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        var (state, session) = await ReadSession(tenant, sessionId, human, ct);
        var goal = await goals.FindGoalAsync(tenant, sessionId, goalId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await ReadAuthority(state, session, goal, new(human), ct); await Unchanged(tenant, human, state.Tenant.Revision, ct); return goal;
    }
    public ValueTask<GoalMutationResult> MutateAsync(Guid tenant, Guid session, Guid goal, TenantSessionInitiator human, GoalCommand command, CancellationToken ct = default) =>
        goals.MutateGoalAsync(new(tenant, session, goal, new(human), command, ids.NewId(), time.GetUtcNow()), ct);
    public async ValueTask<GoalPage<GoalActivity>> ActivityAsync(Guid tenant, Guid session, Guid goal, TenantSessionInitiator human, int page, int pageSize, CancellationToken ct)
    {
        await ReadAsync(tenant, session, goal, human, ct); var result = await goals.ListGoalActivityAsync(tenant, session, goal, page, pageSize, ct);
        await ReadAsync(tenant, session, goal, human, ct); return result;
    }
    public async ValueTask<GoalMutationResult?> RecoverAsync(Guid tenant, Guid session, TenantSessionInitiator human, string key, CancellationToken ct)
    {
        await ReadSession(tenant, session, human, ct); var result = await goals.FindGoalRequestAsync(tenant, session, human.PrincipalId, key, ct);
        if (result is not null) await ReadAsync(tenant, session, result.Goal.GoalId, human, ct); return result;
    }
    public async ValueTask<GoalFocusResult> FocusAsync(Guid tenant, Guid sessionId, Guid goalId, TenantSessionInitiator human, Guid requestId,
        long expectedSecurity, long expectedSession, long expectedGoal, CancellationToken ct)
    {
        var (_, session) = await ReadSession(tenant, sessionId, human, ct);
        var request = new GoalFocus(session.Owner, human, goalId, requestId, expectedSecurity, expectedSession, expectedGoal);
        return control is null ? await goals.FocusGoalAsync(request, ids.NewId(), ct) : await control.FocusGoalAsync(request, ct);
    }
    public async ValueTask<TenantRunReceipt> ContinueAsync(Guid tenant, Guid sessionId, Guid goalId, TenantSessionInitiator human, Guid requestId,
        long expectedSecurity, long expectedSession, long expectedGoal, long expectedPlan, CancellationToken ct)
    {
        var (state, session) = await ReadSession(tenant, sessionId, human, ct);
        var goal = await ReadAsync(tenant, sessionId, goalId, human, ct);
        var text = "Work on explicit goal " + goalId.ToString("D");
        var replay = await ((ITenantRunStore)security).FindTenantAcceptedHumanInputAsync(session.Owner, human.PrincipalId, requestId, text, [], null, AgentCore.Domain.Conversation.SessionMode.Text, ct);
        if (replay is not null)
        {
            if (replay.Run.Admission.GoalWork is not { } original || original.GoalId != goalId || original.GoalRevision != expectedGoal || original.PlanRevision != expectedPlan) throw new SecurityConflictException("goal_request_conflict");
            await Unchanged(tenant, human, state.Tenant.Revision, ct);
            return new(false, replay.Run.AgentRunId, replay.EntryId, replay.Run.ResponseId!.Value, replay.Run.ActivationId,
                replay.Run.Admission.Activation.RootActivationId, replay.SessionRevision, state.Tenant.Revision);
        }
        if (goal.Revision != expectedGoal || goal.PlanRevision != expectedPlan || state.Tenant.Revision != expectedSecurity) throw new SecurityConflictException("stale_goal_revision");
        if (goal.Plan.DeadlineAt is { } deadline && deadline <= time.GetUtcNow()) throw new SecurityConflictException("goal_deadline_elapsed");
        var checkpoint = GoalRules.Next(goal) ?? throw new SecurityConflictException("goal_verification_required");
        var bound = new GoalWorkContext(goal.GoalId, checkpoint.CheckpointId, goal.PlanRevision, goal.Revision, GoalRules.Hash(goal.Plan), sessionId, goal.SpaceId, session.Snapshot.GoalFocusRevision, GoalRules.Hash(checkpoint), session.Snapshot.DurableLastEntrySequence + 1, GoalRules.Task(goal, checkpoint));
        GoalAuthority.Binding(goal, session.Snapshot, bound);
        foreach (var key in new[] { "goals.manage", "agent-runs.create" }) await GoalAuthority.Require(state, human.PrincipalId, key,
            key == "agent-runs.create" ? AgentCore.Domain.Security.AccessScope.ForTenant(tenant) : TenantSessionRules.Scope(tenant, sessionId), iam, ct);
        return await runService.SubmitGoalAsync(tenant, sessionId, human, requestId, text,
            expectedSecurity, expectedSession, bound, ct);
    }
    private async ValueTask<(TenantSecurityState State, TenantSession Session)> ReadSession(Guid tenant, Guid session, TenantSessionInitiator human, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        await GoalAuthority.Require(state, human.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, session), iam, ct);
        return (state, await sessions.FindTenantSessionAsync(tenant, session, ct) ?? throw new AccessDeniedException("resource_unavailable"));
    }
    private async ValueTask ReadAuthority(TenantSecurityState state, TenantSession session, SessionGoal goal, GoalActor actor, CancellationToken ct)
    {
        var agent = await agents.FindTenantAgentAsync(goal.TenantId, session.Owner.AgentInstanceId, ct);
        var space = await spaces.FindSpaceAsync(goal.TenantId, goal.SpaceId, ct);
        var member = (await spaces.ReadMembershipsAsync(goal.TenantId, goal.SpaceId, ct)).SingleOrDefault(m => m.InstanceId == session.Owner.AgentInstanceId);
        await GoalAuthority.RequireAsync(state, session, goal, actor, "goals.read", iam, space, member, agent, null, null, time.GetUtcNow(), false, ct);
    }
    private async ValueTask Unchanged(Guid tenant, TenantSessionInitiator human, long revision, CancellationToken ct)
    { if ((await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct)).Tenant.Revision != revision) throw new SecurityConflictException("stale_revision"); }
}
