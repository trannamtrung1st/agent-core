using AgentCore.Application.Ports;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Security;

public static partial class TenantTaskConsentRules
{
    public static void MatchHumanAdmission(TenantApprovalRequest request, TenantHumanTaskAdmission admission)
    {
        RequireHumanShape(request); var p = request.Operation.TaskDelegation!;
        if (admission.AuditId == Guid.Empty || System.Text.Json.JsonSerializer.Serialize(admission.Task) != System.Text.Json.JsonSerializer.Serialize(p.Task)
            || admission.Session?.SessionId != p.Task.WorkerSessionId || admission.Run?.AgentRunId != p.Task.InitialWorkerRunId
            || admission.Run?.ActivationId != p.WorkerActivationId || admission.Run?.ResponseId != p.WorkerResponseId
            || admission.Session?.Entries.Single().EntryId != p.WorkerEntryId)
            throw new SecurityConflictException("operation_mismatch");
    }
    public static bool IsHuman(TenantApprovalRequest request) => request.Operation.TaskDelegation?.Task.Source.ActorKind == TaskActorKind.Human;
    public static void RequireHumanShape(TenantApprovalRequest request)
    {
        var p = request.Operation.TaskDelegation ?? throw new AccessDeniedException("task_consent_unavailable"); p.Validate();
        var task = p.Task;
        if (task.Source.ActorKind != TaskActorKind.Human || request.Kind != ApprovalKind.Review
            || request.TenantId != task.Source.TenantId || request.SessionId != task.Source.SessionId
            || request.RunId != p.PlannedWorkerRunId || request.RootActivationId != task.Source.RootActivationId
            || request.ExecutorPrincipalId != task.Source.PrincipalId || request.OriginalInitiatorPrincipalId != task.Source.PrincipalId
            || request.Operation.InstanceId != task.Source.InstanceId || request.Operation.SpaceContext is not null
            || TenantApprovalRules.Rebuild(request.Operation) != request.Operation)
            throw new AccessDeniedException("task_consent_unavailable");
    }
    public static async ValueTask RequireHumanSourceAsync(TenantApprovalRequest request, TenantSession source, TenantAgent sourceAgent,
        TenantSecurityState state, IamAuthorizer iam, DateTimeOffset at, CancellationToken ct)
    {
        RequireHumanShape(request); var task = request.Operation.TaskDelegation!.Task;
        TenantSessionRules.RequireExecutor(state, sourceAgent);
        if (source.Owner.TenantId != task.Source.TenantId || source.Owner.SessionId != task.Source.SessionId
            || source.Owner.AgentInstanceId != task.Source.InstanceId || source.Owner.ExecutorPrincipalId != sourceAgent.PrincipalId
            || sourceAgent.Instance.Revision != request.Operation.ExpectedInstanceRevision || sourceAgent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision
            || sourceAgent.Instance.Lifecycle != AgentInstanceLifecycle.Active || source.Snapshot.DurablyDeletedAt is not null
            || source.Snapshot.ArchivedAt is not null || SessionRetention.IsDue(source.Snapshot, at)
            || source.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || source.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending
            || !state.Users.Any(u => u.PrincipalId == task.Source.PrincipalId && u.Status == IdentityStatus.Active))
            throw new AccessDeniedException("task_source_unavailable");
        foreach (var key in new[] { "sessions.read", "sessions.manage", "tasks.delegate" })
            await TaskAuthority.RequireAsync(state, task.Source.PrincipalId, key, TenantSessionRules.Scope(request.TenantId, task.Source.SessionId), iam, ct);
    }
    public static async ValueTask<bool> HumanNeedsReviewAsync(TenantSecurityState state, DelegatedTask task, IamAuthorizer iam, CancellationToken ct)
    {
        if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get("tasks.delegate")).Mode == OperationApprovalMode.Always) return true;
        foreach (var key in new[] { "sessions.create", "agent-runs.create" })
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(state, task.Source.PrincipalId, key, AccessScope.ForTenant(task.Source.TenantId), iam, ct)) return true;
        return false;
    }
}
