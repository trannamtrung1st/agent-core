using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantAccountActionReference(Guid RequestId, Guid OperationId, string ToolName);
public static class TenantAccountActionReferences
{
    public static IReadOnlyList<TenantAccountActionReference> Read(AgentRun run)
    {
        if (!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return [];
        var result = new List<TenantAccountActionReference>();
        foreach (var message in messages!.Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            using var json = JsonDocument.Parse(message.Text); var root = json.RootElement;
            if (!root.TryGetProperty("tenantAccountAction", out var marker) || marker.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("result", out var data) || data.ValueKind == JsonValueKind.Null) continue;
            if (data.ValueKind != JsonValueKind.Object) throw new AccessDeniedException("account_source_changed");
            result.Add(new(root.GetProperty("requestId").GetGuid(), root.GetProperty("operationId").GetGuid(), root.GetProperty("accountActionTool").GetString() ?? ""));
            if (result.Count > 100) throw new AccessDeniedException("account_source_changed");
        }
        return result;
    }
    public static void Receipt(TenantAccountActionReference reference, TenantApprovalRequest request, TenantOperationReceipt receipt,
        TenantApprovalDecision decision, TenantOperationGrant? grant, AgentRun run)
    {
        var action = request.Operation.AccountAction ?? throw new AccessDeniedException("account_source_changed");
        var expectedGrant = receipt.Outcome switch { TenantOperationOutcome.Completed => OperationGrantStatus.Consumed,
            TenantOperationOutcome.Denied => OperationGrantStatus.Revoked, _ => OperationGrantStatus.Indeterminate };
        if (reference.RequestId != request.RequestId || reference.OperationId != request.Operation.OperationId || reference.ToolName != action.ToolName
            || request.Status != TenantApprovalStatus.Approved || request.TenantId != run.Owner.TenantId || request.SessionId != run.SessionId
            || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || request.Operation.InstanceId != run.AgentInstanceId
            || request.RootActivationId != run.Admission.ExecutionContext?.RootActivationId || request.OriginalInitiatorPrincipalId != run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.TenantId != request.TenantId || receipt.InstanceId != request.Operation.InstanceId || receipt.SessionId != run.SessionId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId
            || decision.RequestId != request.RequestId || decision.TenantId != request.TenantId || decision.ActionHash != request.Operation.ActionHash
            || decision.Status != TenantApprovalStatus.Approved || decision.RequestRevision != request.Revision
            || (request.Kind == ApprovalKind.Review ? grant is not null : grant is null || grant.Status != expectedGrant || grant.TenantId != request.TenantId
                || grant.RequestId != request.RequestId || grant.OperationId != receipt.OperationId || grant.ExecutorPrincipalId != request.ExecutorPrincipalId
                || grant.ActionHash != request.Operation.ActionHash)) throw new AccessDeniedException("account_source_changed");
    }
}
