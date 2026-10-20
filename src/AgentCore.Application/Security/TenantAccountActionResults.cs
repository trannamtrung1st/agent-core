using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Historical factual detail, read only after the approval's current compound human
// scope. Absence/compaction never means that an external action did not run.
public static class TenantAccountActionResults
{
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static TenantAccountActionResult? Read(TenantApprovalRequest request, TenantOperationReceipt receipt, AgentRun? run)
    {
        if (request.Operation.AccountAction is not { } action || request.Status != TenantApprovalStatus.Approved
            || receipt.RequestId != request.RequestId || receipt.OperationId != request.Operation.OperationId || receipt.ActionHash != request.Operation.ActionHash
            || receipt.TenantId != request.TenantId || receipt.SessionId != request.SessionId || receipt.InstanceId != request.Operation.InstanceId
            || receipt.ExecutorPrincipalId != request.ExecutorPrincipalId || receipt.RootActivationId != request.RootActivationId) return null;
        if (receipt.Outcome == TenantOperationOutcome.Completed && receipt.ReasonCode == TenantAccountEffectRules.Completed && action.ToolName == ToolCatalog.EmailCreateDraft)
            return new("draft_created", DraftId: action.Draft!.DraftId);
        if (receipt.Outcome == TenantOperationOutcome.Denied && action.ToolName == ToolCatalog.EmailSend && receipt.ReasonCode == "account_draft_already_sent")
            return new("already_sent", DraftId: action.Draft!.DraftId);
        if (run?.Owner.TenantId != request.TenantId || run.AgentRunId != receipt.RunId || run.SessionId != request.SessionId
            || run.Owner.ExecutorPrincipalId != request.ExecutorPrincipalId || run.AgentInstanceId != request.Operation.InstanceId
            || run.Admission.ExecutionContext?.RootActivationId != request.RootActivationId
            || !AgentRunToolCallCheckpoint.TryRead(run.Checkpoint, out var messages)) return null;
        foreach (var message in messages!.Reverse().Where(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.RequestAuthorization))
        {
            try
            {
                using var document = JsonDocument.Parse(message.Text); var root = document.RootElement;
                if (!root.TryGetProperty("tenantAccountAction", out var marker) || marker.ValueKind != JsonValueKind.True
                    || root.GetProperty("requestId").GetGuid() != request.RequestId || root.GetProperty("operationId").GetGuid() != receipt.OperationId
                    || root.GetProperty("accountActionTool").GetString() != action.ToolName || root.GetProperty("outcome").GetString() != JsonNamingPolicy.CamelCase.ConvertName(receipt.Outcome.ToString())
                    || root.GetProperty("reasonCode").GetString() != receipt.ReasonCode) continue;
                var result = root.GetProperty("result").Deserialize<TenantAccountActionResult>(Json);
                if (result is not null) TenantAccountEffectRules.Result(request, receipt.Outcome, result);
                return result;
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException) { }
        }
        return null;
    }
}
