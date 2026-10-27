using System.Text.Json;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Tasks;

/// <summary>Derived only from the exact committed approval, not an inherited Run permission.</summary>
public sealed class TaskConsentAuthority
{
    private TaskConsentAuthority(Guid taskId, bool exceptional) { TaskId = taskId; Exceptional = exceptional; }
    public Guid TaskId { get; }
    public bool Exceptional { get; }
    public static async ValueTask<TaskConsentAuthority> VerifyHumanAsync(DelegatedTask task, TenantApprovalRequest request,
        TenantApprovalDecision? decision, HumanTaskApprovalOutcome? outcome, TenantSecurityState state, IamAuthorizer iam, CancellationToken ct)
    {
        TenantTaskConsentRules.RequireHumanShape(request); var original = request.Operation.TaskDelegation!.Task;
        if (original.TaskId != task.TaskId || JsonSerializer.Serialize(original.Source) != JsonSerializer.Serialize(task.Source)
            || JsonSerializer.Serialize(original.Assignment) != JsonSerializer.Serialize(task.Assignment)
            || original.Request.Fingerprint() != task.Request.Fingerprint() || original.CreatedAt != task.CreatedAt || original.DeadlineAt != task.DeadlineAt
            || request.Status != TenantApprovalStatus.Approved || decision?.Status != TenantApprovalStatus.Approved
            || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.RequestRevision != request.Revision
            || decision.ActionHash != request.Operation.ActionHash || outcome?.Admitted != true
            || outcome.TenantId != request.TenantId || outcome.RequestId != request.RequestId || outcome.OperationId != request.Operation.OperationId
            || outcome.TaskAdmission?.TaskId != task.TaskId || outcome.TaskAdmission.Revision != 1
            || outcome.TaskAdmission.WorkerSessionId != original.WorkerSessionId || outcome.TaskAdmission.InitialWorkerRunId != original.InitialWorkerRunId
            || outcome.RecordedAt < task.CreatedAt || outcome.RecordedAt >= request.ExpiresAt)
            throw new AccessDeniedException("task_consent_unavailable");
        outcome.Validate(); await TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct);
        return new(task.TaskId, false);
    }
    public static async ValueTask<TaskConsentAuthority> VerifyAsync(DelegatedTask task, TenantApprovalRequest request,
        TenantApprovalDecision? decision, TenantOperationGrant? grant, TenantOperationReceipt? receipt,
        TenantSecurityState state, IamAuthorizer iam, CancellationToken ct)
    {
        var original = request.Operation.TaskDelegation?.Task;
        if (original is null || original.TaskId != task.TaskId || original.Source.TenantId != state.Tenant.TenantId
            || JsonSerializer.Serialize(original.Source) != JsonSerializer.Serialize(task.Source)
            || JsonSerializer.Serialize(original.Assignment) != JsonSerializer.Serialize(task.Assignment)
            || original.Request.Fingerprint() != task.Request.Fingerprint() || original.CreatedAt != task.CreatedAt || original.DeadlineAt != task.DeadlineAt
            || request.Status != TenantApprovalStatus.Approved || decision?.Status != TenantApprovalStatus.Approved
            || decision.TenantId != request.TenantId || decision.RequestId != request.RequestId || decision.RequestRevision != request.Revision
            || decision.ActionHash != request.Operation.ActionHash || receipt?.Outcome != TenantOperationOutcome.Completed
            || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.TenantId != task.Source.TenantId || receipt.SessionId != task.Source.SessionId || receipt.RootActivationId != task.Source.RootActivationId
            || receipt.ExecutorPrincipalId != task.Source.PrincipalId || receipt.InstanceId != task.Source.InstanceId
            || receipt.ReasonCode != "task_created" || receipt.TaskAdmission?.TaskId != task.TaskId || receipt.TaskAdmission.Revision != 1
            || receipt.TaskAdmission.WorkerSessionId != original.WorkerSessionId || receipt.TaskAdmission.InitialWorkerRunId != original.InitialWorkerRunId
            || receipt.RecordedAt < task.CreatedAt || receipt.RecordedAt >= request.ExpiresAt
            || request.Kind == ApprovalKind.ExceptionalGrant && (grant is null || grant.Status != OperationGrantStatus.Consumed
                || grant.RequestId != request.RequestId || grant.OperationId != request.Operation.OperationId || grant.ActionHash != request.Operation.ActionHash
                || grant.TenantId != task.Source.TenantId || grant.ExecutorPrincipalId != task.Source.PrincipalId || receipt.RecordedAt >= grant.ExpiresAt)
            || request.Kind == ApprovalKind.Review && grant is not null)
            throw new AccessDeniedException("task_consent_unavailable");
        await TenantTaskConsentRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct);
        return new(task.TaskId, request.Kind == ApprovalKind.ExceptionalGrant);
    }
}
