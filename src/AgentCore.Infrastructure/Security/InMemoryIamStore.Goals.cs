using AgentCore.Application.Agents;
using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantSessionGoalStore
{
    private readonly Dictionary<(Guid Tenant, Guid Goal), SessionGoal> goals = [];
    private readonly List<(GoalActivity Activity, SessionGoal Receipt)> goalActivity = [];
    public ValueTask<SessionGoal?> FindGoalAsync(Guid tenant, Guid session, Guid goal, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(sessionBacking.ReadTenantSession(tenant, session) is not null
            && goals.GetValueOrDefault((tenant, goal)) is { } value && value.SessionId == session ? GoalRules.Freeze(value) : null);
    }
    public ValueTask<GoalPage<SessionGoal>> ListGoalsAsync(Guid tenant, Guid session, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); GoalBounds(page, pageSize); lock (_gate)
        {
            var all = sessionBacking.ReadTenantSession(tenant, session) is null ? [] : goals.Values.Where(g => g.TenantId == tenant && g.SessionId == session).OrderBy(g => g.CreatedAt).ThenBy(g => g.GoalId).ToArray();
            return ValueTask.FromResult(new GoalPage<SessionGoal>(GoalRules.Freeze(all.Skip(page * pageSize).Take(pageSize).ToArray()), all.Length, page, pageSize));
        }
    }
    public ValueTask<GoalPage<GoalActivity>> ListGoalActivityAsync(Guid tenant, Guid session, Guid goal, int page, int pageSize, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); GoalBounds(page, pageSize); lock (_gate)
        {
            var all = sessionBacking.ReadTenantSession(tenant, session) is null ? [] : goalActivity.Where(a => a.Activity.TenantId == tenant && a.Activity.SessionId == session && a.Activity.GoalId == goal).OrderByDescending(a => a.Activity.Revision).Select(a => a.Activity).ToArray();
            return ValueTask.FromResult(new GoalPage<GoalActivity>(GoalRules.Freeze(all.Skip(page * pageSize).Take(pageSize).ToArray()), all.Length, page, pageSize));
        }
    }
    public ValueTask<GoalMutationResult?> FindGoalRequestAsync(Guid tenant, Guid session, Guid actor, string requestKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            var item = goalActivity.SingleOrDefault(a => a.Activity.TenantId == tenant && a.Activity.SessionId == session && a.Activity.ActorId == actor && a.Activity.RequestKey == requestKey);
            return ValueTask.FromResult<GoalMutationResult?>(item.Activity is null || sessionBacking.ReadTenantSession(tenant, session) is null ? null : new(GoalRules.Freeze(item.Receipt), _tenants[tenant].Tenant.Revision, true));
        }
    }
    public ValueTask<GoalMutationResult> MutateGoalAsync(GoalMutation request, CancellationToken ct = default)
    {
        request = request with { Command = GoalRules.Freeze(request.Command) };
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var state = _tenants.GetValueOrDefault(request.TenantId) ?? throw new AccessDeniedException("tenant_unavailable");
            var session = sessionBacking.ReadTenantSession(request.TenantId, request.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var before = goals.GetValueOrDefault((request.TenantId, request.GoalId));
            RequireGoalActor(request, state, session, before, GoalTransitions.Permission(request.Command), true, ct);
            if (request.Command.Kind == GoalCommandKind.Create && request.Command.Confirm)
                GoalAuthority.Require(state, request.Actor.PrincipalId, "goals.manage", TenantSessionRules.Scope(request.TenantId, request.SessionId), new(this), ct).AsTask().GetAwaiter().GetResult();
            var replay = goalActivity.SingleOrDefault(a => a.Activity.TenantId == request.TenantId && a.Activity.SessionId == request.SessionId && a.Activity.ActorId == request.Actor.PrincipalId && a.Activity.RequestKey == request.Command.RequestKey);
            if (replay.Activity is not null)
            {
                if (replay.Activity.Fingerprint != GoalTransitions.Fingerprint(request)) throw new SecurityConflictException("goal_request_conflict");
                return ValueTask.FromResult(new GoalMutationResult(GoalRules.Freeze(replay.Receipt), state.Tenant.Revision, true));
            }
            if (state.Tenant.Revision != request.Command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
            if (before is null && goals.Values.Count(g => g.TenantId == request.TenantId && g.SessionId == request.SessionId) >= GoalRules.MaxGoals) throw new ArgumentException("Session Goal limit reached.");
            var (next, activity) = GoalTransitions.ApplyAsync(request, before, session.Snapshot.ContextSelection!.SpaceId,
                (goal, checkpoint, evidence) => QualifyGoalEvidence(state, request.Actor, goal, checkpoint, evidence, ct)).AsTask().GetAwaiter().GetResult();
            var revision = checked(state.Tenant.Revision + 1);
            goals[(next.TenantId, next.GoalId)] = GoalRules.Freeze(next); goalActivity.Add((activity, GoalRules.Freeze(next)));
            _tenants[next.TenantId] = state with { Tenant = state.Tenant with { Revision = revision } };
            _audit.Add(new(activity.ActivityId, next.TenantId, request.Actor.PrincipalId, "goals." + request.Command.Kind.ToString().ToLowerInvariant(), next.GoalId.ToString("D"), "allowed", "goal_changed", request.At));
            return ValueTask.FromResult(new GoalMutationResult(GoalRules.Freeze(next), revision));
        }
    }
    private void RequireGoalActor(GoalMutation request, TenantSecurityState state, TenantSession session, SessionGoal? goal, string permission, bool mutate, CancellationToken ct)
    {
        if (request.Actor.Human is { } human) RequireHumanSession(request.TenantId, human);
        var spaceId = goal?.SpaceId ?? session.Snapshot.ContextSelection!.SpaceId;
        var agent = FindTenantAgentAsync(request.TenantId, session.Owner.AgentInstanceId, ct).Result;
        var run = request.Actor.Agent is { } frame ? sessionBacking.ReadTenantRun(request.TenantId, frame.RunId) : null;
        if (request.Actor.Agent is not null && request.Command.Kind is GoalCommandKind.Progress or GoalCommandKind.Verify
            && run?.Admission.GoalWork?.CheckpointId != request.Command.CheckpointId) throw new AccessDeniedException("goal_checkpoint_binding_required");
        GoalAuthority.RequireAsync(state, session, goal, request.Actor, permission, new(this), FindSpaceAsync(request.TenantId, spaceId, ct).Result,
            _spaceMemberships.GetValueOrDefault((request.TenantId, spaceId, session.Owner.AgentInstanceId)), agent, run,
            agent is null ? null : InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), (clock ?? TimeProvider.System).GetUtcNow(), mutate, ct).AsTask().GetAwaiter().GetResult();
    }
    private ValueTask<bool> QualifyGoalEvidence(TenantSecurityState state, GoalActor actor, SessionGoal goal, GoalCheckpointPlan checkpoint, GoalEvidence evidence, CancellationToken ct)
    {
        var run = evidence.RunId is { } id ? sessionBacking.ReadTenantRun(goal.TenantId, id) : null;
        var attempt = evaluationAttempts.GetValueOrDefault((goal.TenantId, evidence.SourceId));
        var suite = attempt is null ? null : Suite(goal.TenantId, attempt.SuiteId, attempt.SuiteVersion);
        var artifact = artifactBacking?.FindTenantAsync(goal.TenantId, goal.SessionId, evidence.SourceId, ct).Result;
        return GoalAuthority.EvidenceAsync(state, actor, goal, checkpoint, evidence, run, attempt, suite, artifact, new(this), ct);
    }
    private static void GoalBounds(int page, int size) { if (page is < 0 or > 100_000 || size is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page)); }
    private void RetireGoals(Guid tenant, Guid session)
    {
        if (sessionBacking.ReadRetainedTenantBackgroundSession(tenant, session)?.Snapshot.DurablyDeletedAt is null) return;
        foreach (var key in goals.Where(g => g.Key.Tenant == tenant && g.Value.SessionId == session).Select(g => g.Key).ToArray()) goals.Remove(key);
        goalActivity.RemoveAll(a => a.Activity.TenantId == tenant && a.Activity.SessionId == session);
    }
}
