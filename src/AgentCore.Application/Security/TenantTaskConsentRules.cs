using AgentCore.Application.Tasks;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using System.Text.Json;

namespace AgentCore.Application.Security;

/// <summary>Consent covers one assignment. The target's independent execution permissions remain raw checks.</summary>
public static partial class TenantTaskConsentRules
{
    public const string AuditAction = "operations.tasks.admit";
    public static void MatchAdmission(TenantApprovalRequest request, TaskAdmission admission)
    {
        admission.Actor.Validate();
        var p = request.Operation.TaskDelegation ?? throw new SecurityConflictException("operation_mismatch"); p.Validate();
        if (admission.TenantId != request.TenantId || admission.SourceSessionId != p.Task.Source.SessionId || admission.AuditId == Guid.Empty
            || JsonSerializer.Serialize(admission.Task) != JsonSerializer.Serialize(p.Task)
            || admission.Request.RequestKey != p.Task.Request.RequestKey || admission.Request.Fingerprint() != p.Task.Request.Fingerprint()
            || admission.Actor.Agent is not { } actor || actor.Owner.TenantId != request.TenantId || actor.Owner.AgentInstanceId != p.Task.Source.InstanceId
            || actor.Owner.ExecutorPrincipalId != p.Task.Source.PrincipalId || actor.SessionId != p.Task.Source.SessionId
            || actor.RunId != p.Task.Source.RunId || actor.Generation != p.SourceGeneration || admission.Actor.Tool != "tasks.delegate"
            || admission.WorkerSession?.SessionId != p.Task.WorkerSessionId || admission.WorkerRun?.AgentRunId != p.Task.InitialWorkerRunId
            || admission.WorkerRun?.ActivationId != p.WorkerActivationId || admission.WorkerRun?.ResponseId != p.WorkerResponseId
            || admission.WorkerSession?.Entries.Single().EntryId != p.WorkerEntryId)
            throw new SecurityConflictException("operation_mismatch");
    }
    public static async ValueTask RequireModelSourceAsync(TenantApprovalRequest request, AgentRun run, AgentRun original, TenantSession source,
        TenantAgent agent, AgentDefinition current, TenantSecurityState state, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct, bool creation = false)
    {
        var prepared = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_review_unavailable"); prepared.Validate();
        var task = prepared.Task;
        TenantApprovalRules.Validate(request, run, agent, source: original.AgentRunId == run.AgentRunId ? null : original);
        TenantWorkspaceEffectRules.Session(run, agent, source); TenantSessionRules.RequireExecutor(state, agent);
        if (task.Source.ActorKind != TaskActorKind.Agent || task.Source.RunId != original.AgentRunId
            || creation && (original.Claim?.Generation != prepared.SourceGeneration || original.AgentRunId != run.AgentRunId)
            || run.Status is not (AgentRunStatus.Running or AgentRunStatus.WaitingForApproval) || run.CancellationRequested
            || source.Snapshot.DurablyDeletedAt is not null || SessionRetention.IsDue(source.Snapshot, at)
            || task.Source.PrincipalId != original.Owner.ExecutorPrincipalId || task.Source.InstanceId != original.AgentInstanceId
            || task.Source.SessionId != original.SessionId || task.Source.ActivationId != original.ActivationId
            || task.Source.RootActivationId != original.Admission.Activation.RootActivationId
            || task.Source.ParentTaskId != original.Admission.DelegatedTask?.TaskId
            || task.Source.OriginalInitiatorPrincipalId != original.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
            || !RolePermissions.AllowsTool(original.Admission.Configuration!.Definition, "tasks.delegate") || !RolePermissions.AllowsTool(current, "tasks.delegate"))
            throw new AccessDeniedException("task_source_unavailable");
        await TaskAuthority.RequireAsync(state, agent.PrincipalId, "sessions.read", TenantSessionRules.Scope(request.TenantId, source.Owner.SessionId), iam, ct);
        if (request.Kind != ApprovalKind.ExceptionalGrant)
            await TaskAuthority.RequireAsync(state, agent.PrincipalId, "tasks.delegate", TenantSessionRules.Scope(request.TenantId, source.Owner.SessionId), iam, ct);
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(PreparedTenantOperation operation)
    {
        var task = operation.TaskDelegation?.Task ?? throw new AccessDeniedException("task_review_unavailable");
        yield return ("tasks.delegate", TenantSessionRules.Scope(task.Source.TenantId, task.Source.SessionId));
        yield return ("sessions.create", AccessScope.ForTenant(task.Source.TenantId));
        yield return ("agent-runs.create", AccessScope.ForTenant(task.Source.TenantId));
    }
    public static async ValueTask<bool> CompoundReviewAsync(TenantSecurityState state, Guid? requester, PreparedTenantOperation operation,
        IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(operation).Where(t => t.Permission != "tasks.delegate"))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct)) return true;
        return false;
    }
    public static async ValueTask ReadAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        var task = request.Operation.TaskDelegation?.Task ?? throw new AccessDeniedException("approval_unavailable");
        await TaskAuthority.RequireAsync(state, human, "models.read", AccessScope.ForTenant(request.TenantId), iam, ct);
        foreach (var id in new[] { task.Source.InstanceId, task.Assignment.InstanceId }.Distinct())
            await TaskAuthority.RequireAsync(state, human, "agents.read", AccessScope.ForResource(request.TenantId, "agent", id.ToString("D")), iam, ct);
        await TaskAuthority.RequireAsync(state, human, "sessions.read", TenantSessionRules.Scope(request.TenantId, task.Source.SessionId), iam, ct);
        await TaskAuthority.RequireAsync(state, human, "spaces.read", TenantSpaceService.Scope(request.TenantId, task.Assignment.SpaceId), iam, ct);
        if (task.Assignment.EnvironmentId is not null)
            await TaskAuthority.RequireAsync(state, human, "environments.read", TenantSpaceService.Scope(request.TenantId, task.Assignment.SpaceId), iam, ct);
        foreach (var (key, scope) in Targets(request.Operation)) await TaskAuthority.RequireAsync(state, human, key, scope, iam, ct);
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state, TenantApprovalRequest request, Guid human, IamAuthorizer iam, CancellationToken ct)
    {
        await ReadAsync(state, request, human, iam, ct);
        foreach (var (key, scope) in Targets(request.Operation))
        {
            if (key != request.Permission && !await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview && human == request.OriginalInitiatorPrincipalId)
                throw new AccessDeniedException("independent_reviewer_required");
            await TaskAuthority.RequireAsync(state, human, "approvals.approve", scope, iam, ct);
            if (key == request.Permission && request.Kind == ApprovalKind.ExceptionalGrant)
                await TaskAuthority.RequireAsync(state, human, "approvals.grant_exception", scope, iam, ct);
        }
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantApprovalRequest request, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (key, scope) in Targets(request.Operation))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).IndependentReview
                && (key == request.Permission || await TenantSandboxReadOperationRules.NeedsReviewAsync(state, request.OriginalInitiatorPrincipalId, key, scope, iam, ct))) return true;
        return false;
    }
}
