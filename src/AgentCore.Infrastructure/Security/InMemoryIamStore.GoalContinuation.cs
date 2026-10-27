using AgentCore.Application.Goals;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Goals;
using AgentCore.Infrastructure.Persistence;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore : ITenantGoalContinuationStore
{
    public ValueTask<IReadOnlyList<GoalContinuationCandidate>> ListGoalContinuationCandidatesAsync(int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate)
        {
            var result = new List<GoalContinuationCandidate>(); var at = (clock ?? TimeProvider.System).GetUtcNow();
            foreach (var goal in goals.Values.Where(g => g.Status == GoalStatus.Active && g.Plan.AutoContinuation is { Enabled: true, Until: { } until } a && until > at && g.AutoRunsUsed < a.MaxRuns).OrderBy(g => g.UpdatedAt))
            {
                var session = sessionBacking.ReadTenantSession(goal.TenantId, goal.SessionId); if (session is null) continue;
                var siblings = sessionBacking.AgentRuns.Runs.Values.Where(r => r.Owner.TenantId == goal.TenantId && r.SessionId == goal.SessionId).ToArray();
                if (siblings.Any(r => !r.IsTerminal)) continue;
                var source = siblings.Where(r => r.Admission.GoalWork?.GoalId == goal.GoalId && goal.Checkpoints.Any(c => c.Status == GoalCheckpointStatus.Verified && c.SourceRunId == r.AgentRunId))
                    .OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.AgentRunId)
                    .FirstOrDefault(r => !siblings.Any(child => child.Admission.Activation.Kind == ActivationKind.GoalContinuation && child.Admission.Activation.SourceAgentRunId == r.AgentRunId));
                if (source is null || !GoalContinuationRules.CanContinue(goal, source, session.Snapshot, at)
                    || siblings.Any(r => r.Admission.Activation.Kind == ActivationKind.GoalContinuation && r.Admission.Activation.SourceAgentRunId == source.AgentRunId)) continue;
                result.Add(new(GoalRules.Freeze(goal), source, session)); if (result.Count >= Math.Clamp(limit, 1, 8)) break;
            }
            return ValueTask.FromResult<IReadOnlyList<GoalContinuationCandidate>>(result);
        }
    }
    public ValueTask<bool> TryAdmitGoalContinuationAsync(GoalContinuationAdmission admission, Guid activityId, DateTimeOffset at, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate)
        {
            var run = admission.Run; var tenant = run.Owner.TenantId!.Value;
            var source = sessionBacking.ReadTenantRun(tenant, admission.SourceRunId) ?? throw new AccessDeniedException("resource_unavailable");
            var goal = goals.GetValueOrDefault((tenant, run.Admission.GoalWork!.GoalId)) ?? throw new AccessDeniedException("resource_unavailable");
            var state = _tenants[tenant]; var session = sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var all = sessionBacking.AgentRuns; if (all.ByAdmission.ContainsKey((run.SessionId, run.Admission.Activation.DedupeKey))) return ValueTask.FromResult(false);
            GoalContinuationRules.Admission(admission, goal, source, session, state.Tenant.Revision, all.Runs.Values.Where(r => r.SessionId == source.SessionId),
                sessionBacking.ReadTenantConversation(tenant, source.SessionId, null, null, 100)?.History.Items ?? [], at);
            var agent = FindTenantAgentAsync(tenant, source.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            GoalContinuationService.RequireAsync(state, agent, source, new(this), ct).AsTask().GetAwaiter().GetResult();
            RequireSpaceExecutionSource(run, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            if (run.Admission.Configuration?.InstanceRevision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
            var next = goal with { Revision = goal.Revision + 1, AutoRunsUsed = goal.AutoRunsUsed + 1, UpdatedAt = at,
                Checkpoints = goal.Checkpoints.Select(c => c.CheckpointId == run.Admission.GoalWork!.CheckpointId ? c with { Status = GoalCheckpointStatus.InProgress, SourceRunId = run.AgentRunId } : c).ToArray() };
            var activity = new GoalActivity(activityId, tenant, source.SessionId, goal.GoalId, agent.PrincipalId, run.Admission.Activation.DedupeKey,
                GoalRules.Hash(run.Admission.GoalWork), "AutoContinuation", next.Revision, goal.PlanRevision, at, run.Admission.GoalWork.CheckpointId, run.AgentRunId);
            all.Runs.Add(run.AgentRunId, run); all.Activations.Add(run.ActivationId, AgentRunStoreMapping.ToActivationRecord(session.Snapshot, run));
            all.ByAdmission.Add((run.SessionId, run.Admission.Activation.DedupeKey), run.AgentRunId);
            goals[(tenant, goal.GoalId)] = next; goalActivity.Add((activity, GoalRules.Freeze(next)));
            _tenants[tenant] = state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
            _audit.Add(new(activityId, tenant, agent.PrincipalId, "goals.continue", goal.GoalId.ToString("D"), "allowed", "goal_successor_admitted", at));
            return ValueTask.FromResult(true);
        }
    }
}
