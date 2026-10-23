using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantHttpReference(Guid RequestId,Guid OperationId,TenantHttpResult Result,ModelToolCall Call);
public static class TenantHttpReferences
{
    private static bool Exact(JsonElement value,params string[] keys)=>value.ValueKind==JsonValueKind.Object && value.EnumerateObject().Count()==keys.Length
        && value.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==keys.Length && keys.All(k=>value.TryGetProperty(k,out _));
    public static IReadOnlyList<TenantHttpReference> Read(AgentRun run)
    {
        if(!AgentRunToolCallCheckpoint.TryRead(run.Checkpoint,out var messages))return [];
        try
        {
            var result=new List<TenantHttpReference>();
            foreach(var message in messages!.Where(m=>m.Role==ModelRole.Tool && m.Name is ToolCatalog.HttpRequest or ToolCatalog.RequestAuthorization))
            {
                using var json=JsonDocument.Parse(message.Text);var root=json.RootElement;
                var call=messages!.Where(m=>m.Role==ModelRole.Assistant).SelectMany(m=>m.ToolCalls ?? []).LastOrDefault(c=>c.Id==message.ToolCallId && c.Name==message.Name);
                if(message.Name==ToolCatalog.HttpRequest)
                {
                    if(call is null)throw new ArgumentException();
                    if(TenantPublicWebReadRules.CoreError(root,call))continue;
                    if(!Exact(root,"requiresApproval","reasonCode") || root.GetProperty("requiresApproval").ValueKind!=JsonValueKind.True || root.GetProperty("reasonCode").GetString()!="http_exact_review_required")throw new ArgumentException();
                    continue;
                }
                var marked=root.TryGetProperty("tenantHttpRequest",out var marker) && marker.ValueKind==JsonValueKind.True;
                using var arguments=call is null?null:JsonDocument.Parse(call.ArgumentsJson);
                var intent=arguments is not null && arguments.RootElement.TryGetProperty("operation",out var operation) && operation.ValueKind==JsonValueKind.String && operation.GetString()=="send_http_request";
                if(!marked)
                {
                    if(!intent)continue;
                    if(call is not null && TenantPublicWebReadRules.CoreError(root,call))continue;
                    if(!Exact(root,"decision","reasonCode","requestId","applied") || root.GetProperty("decision").GetString() is not ("denied" or "requiresApproval")
                        || root.GetProperty("applied").ValueKind!=JsonValueKind.False || root.GetProperty("reasonCode").GetString() is not ("no_eligible_approver" or "already_pending" or "previously_rejected" or "previously_expired" or "previously_cancelled" or "already_approved" or "permission_denied"))throw new ArgumentException();
                    continue;
                }
                if(!intent || call is null || !Exact(root,"tenantHttpRequest","approvalStatus","outcome","reasonCode","requestId","operationId","applied","replayAllowed","response","bodyUnavailable")
                    || root.GetProperty("approvalStatus").GetString() is not ("approved" or "rejected" or "expired" or "cancelled")
                    || root.GetProperty("outcome").GetString() is not ("completed" or "denied" or "indeterminate")
                    || root.GetProperty("replayAllowed").ValueKind!=JsonValueKind.False)throw new ArgumentException();
                var reason=root.GetProperty("reasonCode").GetString() ?? "";
                if(reason is not (TenantHttpEffectRules.Completed or TenantHttpEffectRules.Uncertain or "approval_rejected" or "approval_cancelled" or "approval_expired") && TenantHttpEffectRules.Denial(reason)!=reason)throw new ArgumentException();
                var data=root.GetProperty("response");
                if(data.ValueKind==JsonValueKind.Null)
                {
                    if(root.GetProperty("bodyUnavailable").ValueKind!=JsonValueKind.True)throw new ArgumentException();
                    continue;
                }
                if(!Exact(data,"statusCode","finalUrl","contentType","body","truncated","bodyRepresentation","untrusted") || root.GetProperty("approvalStatus").GetString()!="approved"
                    || root.GetProperty("outcome").GetString()!="completed" || reason!=TenantHttpEffectRules.Completed || root.GetProperty("applied").ValueKind!=JsonValueKind.True
                    || root.GetProperty("bodyUnavailable").ValueKind!=JsonValueKind.False || data.GetProperty("untrusted").ValueKind!=JsonValueKind.True
                    || data.GetProperty("bodyRepresentation").GetString()!="boundedUtf8Projection")throw new ArgumentException();
                var value=JsonSerializer.Deserialize<TenantHttpResult>(data,new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException();
                TenantHttpEffectRules.Validate(TenantHttpEffectRules.Metadata(value));
                result.Add(new(root.GetProperty("requestId").GetGuid(),root.GetProperty("operationId").GetGuid(),value,call));if(result.Count>100)throw new ArgumentException();
            }
            return result;
        }
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new AccessDeniedException("http_source_changed"); }
    }
    public static void Receipt(TenantHttpReference reference,TenantApprovalRequest request,TenantOperationReceipt receipt,TenantApprovalDecision decision,TenantOperationGrant? grant,AgentRun run)
    {
        if(request.Operation.HttpRequest is null || !TenantApprovalToolContract.Parse(reference.Call).Matches(request.Operation) || request.Status!=TenantApprovalStatus.Approved || reference.RequestId!=request.RequestId || reference.OperationId!=request.Operation.OperationId
            || request.TenantId!=run.Owner.TenantId || request.SessionId!=run.SessionId || request.ExecutorPrincipalId!=run.Owner.ExecutorPrincipalId || request.Operation.InstanceId!=run.AgentInstanceId
            || request.RootActivationId!=run.Admission.ExecutionContext?.RootActivationId || request.OriginalInitiatorPrincipalId!=run.Admission.ExecutionContext.OriginalInitiatorPrincipalId
            || receipt.RequestId!=request.RequestId || receipt.OperationId!=request.Operation.OperationId || receipt.ActionHash!=request.Operation.ActionHash || receipt.TenantId!=request.TenantId
            || receipt.InstanceId!=run.AgentInstanceId || receipt.SessionId!=run.SessionId || receipt.ExecutorPrincipalId!=request.ExecutorPrincipalId || receipt.RootActivationId!=request.RootActivationId
            || receipt.Outcome!=TenantOperationOutcome.Completed || receipt.ReasonCode!=TenantHttpEffectRules.Completed || receipt.Http!=TenantHttpEffectRules.Metadata(reference.Result)
            || decision.RequestId!=request.RequestId || decision.TenantId!=request.TenantId || decision.ActionHash!=request.Operation.ActionHash || decision.Status!=TenantApprovalStatus.Approved || decision.RequestRevision!=request.Revision)
            throw new AccessDeniedException("http_source_changed");
        TenantHttpEffectRules.RequireGrant(request,grant,OperationGrantStatus.Consumed);
    }
}
