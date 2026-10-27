using AgentCore.Application.Agents;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Goals;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Goals;

public static class GoalAuthority
{
    public static async ValueTask RequireAsync(TenantSecurityState state, TenantSession session, SessionGoal? goal, GoalActor actor,
        string permission, IamAuthorizer iam, Space? space, SpaceMembership? membership, TenantAgent? agent,
        AgentRun? run, AgentDefinition? currentDefinition, DateTimeOffset at, bool mutate, CancellationToken ct)
    {
        if ((actor.Human is null) == (actor.Agent is null) || state.Tenant.TenantId != session.Owner.TenantId
            || goal is not null && (goal.TenantId != session.Owner.TenantId || goal.SessionId != session.Owner.SessionId)) throw new AccessDeniedException("resource_unavailable");
        var scope = TenantSessionRules.Scope(state.Tenant.TenantId, session.Owner.SessionId);
        foreach (var key in new[] { "sessions.read", "goals.read", permission }.Distinct()) await Require(state, actor.PrincipalId, key, scope, iam, ct);
        var spaceId = goal?.SpaceId ?? session.Snapshot.ContextSelection?.SpaceId ?? throw new AccessDeniedException("space_context_required");
        if (space is null || space.SpaceId != spaceId || space.TenantId != state.Tenant.TenantId || space.DeletedAt is not null) throw new AccessDeniedException("space_unavailable");
        await Require(state, actor.PrincipalId, "spaces.read", TenantSpaceService.Scope(state.Tenant.TenantId, spaceId), iam, ct);
        if (mutate)
        {
            if (session.Snapshot.ContextSelection?.SpaceId != spaceId) throw new SecurityConflictException("goal_space_mismatch");
            if (agent is null || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || membership?.InstanceId != agent.Instance.InstanceId
                || membership.SpaceId != spaceId) throw new AccessDeniedException("space_membership_unavailable");
            TenantSessionRules.RequireExecutor(state, agent);
            TenantHumanInputRules.Eligible(session.Snapshot);
            if (SessionRetention.IsDue(session.Snapshot, at)) throw new AccessDeniedException("session_expired");
        }
        if (actor.Agent is { } frame)
        {
            if (run is null || agent is null || currentDefinition is null || actor.Tool is not ("goals.read" or "goals.plan" or "goals.progress" or "goals.verify")) throw new AccessDeniedException("goal_tool_unavailable");
            TenantRuntimeRules.Require(frame, session.Owner, run, at);
            if (!RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, actor.Tool) || !RolePermissions.AllowsTool(currentDefinition, actor.Tool)
                || run.Admission.Configuration.SpaceContext?.SpaceId != spaceId || permission == "goals.accept") throw new AccessDeniedException("goal_tool_unavailable");
            if (mutate && actor.Tool is "goals.progress" or "goals.verify" && (run.Admission.GoalWork is not { } bound || bound.GoalId != goal?.GoalId || bound.PlanRevision != goal.PlanRevision))
                throw new AccessDeniedException("goal_run_binding_required");
            if (run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } human)
                foreach (var key in new[] { "sessions.read", "goals.read", permission }.Distinct()) await Require(state, human, key, scope, iam, ct);
            if (mutate && TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).Mode == OperationApprovalMode.Always)
                throw new AccessDeniedException("goal_human_review_required");
        }
    }
    public static async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, IamAuthorizer iam, CancellationToken ct)
    { var decision = await iam.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }

    public static async ValueTask<bool> EvidenceAsync(TenantSecurityState state, GoalActor actor, SessionGoal goal, GoalCheckpointPlan checkpoint,
        GoalEvidence evidence, AgentRun? run, EvaluationAttempt? attempt, EvaluationSuiteVersion? suite, ArtifactRecord? artifact,
        IamAuthorizer iam, CancellationToken ct)
    {
        if (evidence.Kind == GoalVerificationKind.HumanAcceptance)
            return evidence.PrincipalId is { } id && evidence.AcceptedAt is not null && !string.IsNullOrWhiteSpace(evidence.Reason)
                && (await iam.CheckTenantAsync(state, id, PermissionCatalog.Get("goals.accept"), TenantSessionRules.Scope(goal.TenantId, goal.SessionId), ct)).Allowed;
        if (run is not null && (run.Owner.TenantId != goal.TenantId || run.SessionId != goal.SessionId || run.Admission.GoalWork?.GoalId != goal.GoalId
            || run.Admission.GoalWork.CheckpointId != checkpoint.CheckpointId)) throw new AccessDeniedException("resource_unavailable");
        if (attempt is not null && (attempt.TenantId != goal.TenantId || attempt.SubjectId != evidence.RunId)) throw new AccessDeniedException("resource_unavailable");
        if (run is null || run.Owner.TenantId != goal.TenantId || run.SessionId != goal.SessionId || run.Status != AgentRunStatus.Completed
            || run.CancellationRequested || run.RequiresEffectReconciliation || run.Admission.GoalWork is not { } binding
            || binding.GoalId != goal.GoalId || binding.CheckpointId != checkpoint.CheckpointId || binding.SpaceId != goal.SpaceId
            || binding.CheckpointHash != GoalRules.Hash(checkpoint) || evidence.RunId != run.AgentRunId) return false;
        foreach (var principal in new[] { actor.PrincipalId }.Concat(actor.Agent is not null && run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } original ? new[] { original } : []))
        {
            if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get("agent-runs.read"), TenantRunRules.Scope(goal.TenantId, run.AgentRunId), ct)).Allowed) return false;
            if (evidence.Kind == GoalVerificationKind.EvaluationPass)
            {
                var requirement = checkpoint.Verification.SingleOrDefault(v => v.Kind == GoalVerificationKind.EvaluationPass);
                if (requirement is null || attempt is null || suite is null || suite.Archived || suite.DraftId is not null
                    || attempt.TenantId != goal.TenantId || attempt.AttemptId != evidence.SourceId || attempt.SubjectKind != EvaluationSubjectKind.AgentRun
                    || attempt.SubjectId != run.AgentRunId || attempt.SubjectRevision != run.Revision || attempt.Status != EvaluationStatus.Completed
                    || attempt.Outcome != EvaluationOutcome.Passed || attempt.CommitSequence is null || attempt.SuiteId != requirement.SuiteId
                    || attempt.SuiteVersion != requirement.SuiteVersion || attempt.SuiteHash != requirement.SuiteHash || suite.ContentHash != requirement.SuiteHash
                    || attempt.SourceFingerprint != EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status })) return false;
                if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get("evaluations.read"), EvaluationStoreRules.Scope(goal.TenantId, suite.SuiteId), ct)).Allowed) return false;
            }
            else if (evidence.Kind == GoalVerificationKind.SourceReceipt)
            {
                if (artifact is null || artifact.ArtifactId != evidence.SourceId || artifact.TenantId != goal.TenantId || artifact.SessionId != goal.SessionId
                    || artifact.AgentRunId != run.AgentRunId || artifact.Sha256Hex.Length != 64) return false;
                if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get("artifacts.read"), AccessScope.ForResource(goal.TenantId, "artifact", artifact.ArtifactId.ToString("D")), ct)).Allowed) return false;
            }
            else return false;
        }
        return true;
    }
    public static void Binding(SessionGoal goal, SessionSnapshot session, GoalWorkContext bound)
    {
        if (goal.Status != GoalStatus.Active || goal.PendingPlan is not null
            || goal.GoalId != bound.GoalId || goal.SessionId != bound.SessionId || goal.SpaceId != bound.SpaceId
            || session.SessionId != goal.SessionId || session.ContextSelection?.SpaceId != goal.SpaceId || session.FocusedGoalId != goal.GoalId
            || session.GoalFocusRevision != bound.FocusRevision || goal.PlanRevision != bound.PlanRevision || goal.Revision != bound.GoalRevision
            || GoalRules.Hash(goal.Plan) != bound.PlanHash || GoalRules.Next(goal)?.CheckpointId != bound.CheckpointId
            || bound.CheckpointHash != GoalRules.Hash(goal.Plan.Checkpoints.Single(c => c.CheckpointId == bound.CheckpointId))
            || bound.TaskJson != GoalRules.Task(goal, goal.Plan.Checkpoints.Single(c => c.CheckpointId == bound.CheckpointId)))
            throw new SecurityConflictException("stale_goal_binding");
    }
}
