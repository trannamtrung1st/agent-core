using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantHttpContinuationProjection
{
    public static AgentRunCheckpoint Complete(AgentRunCheckpoint checkpoint,ModelToolCall call,TenantApprovalRequest request,TenantOperationOutcome outcome,string reason,TenantHttpResult? result=null)=>
        AgentRunToolCallCheckpoint.CompleteTenantAuthorization(checkpoint,call,JsonSerializer.Serialize(new {
            tenantHttpRequest=true,approvalStatus=JsonNamingPolicy.CamelCase.ConvertName(request.Status.ToString()),outcome=JsonNamingPolicy.CamelCase.ConvertName(outcome.ToString()),reasonCode=reason,
            requestId=request.RequestId,operationId=request.Operation.OperationId,applied=outcome==TenantOperationOutcome.Completed?(bool?)true:outcome==TenantOperationOutcome.Denied?false:null,
            replayAllowed=false,response=result is null?null:new { statusCode=result.StatusCode,finalUrl=result.FinalUrl,contentType=result.ContentType,body=result.Body,truncated=result.Truncated,
                bodyRepresentation="boundedUtf8Projection",untrusted=true },bodyUnavailable=result is null }));
    public static AgentRun Save(AgentRun before,AgentRun saved,TenantApprovalRequest request,TenantOperationOutcome outcome,string reason,DateTimeOffset at,TenantHttpResult? result)
    {
        if(saved.Status!=AgentRunStatus.Running || saved.CancellationRequested || saved.Claim is null || saved.Claim.LeaseExpiresAtUtc<=at || before.Claim?.Generation!=before.SideEffect.ExternalEffect?.Generation
            || saved.Checkpoint is not { } checkpoint || !AgentRunToolCallCheckpoint.TryRead(checkpoint,out var messages))return saved;
        var pending=AgentRunToolCallCheckpoint.PendingCalls(messages!);if(pending.Count==0)return saved;
        try
        {
            TenantRunContinuationService.ValidateAuthorizationCheckpoint(saved,request,requirePending:true);
            AgentRunCheckpoint complete;
            try { complete=Complete(checkpoint.PauseBudgetClock(at),pending[0],request,outcome,reason,result); }
            catch(ArgumentException) when(result is not null) { complete=Complete(checkpoint.PauseBudgetClock(at),pending[0],request,outcome,reason); }
            var next=saved.SaveCheckpoint(saved.Revision,saved.Claim.Generation,complete,null,at);
            return next.AwaitApprovedExternalResultDelivery(next.Revision,saved.Claim.Generation,request.RequestId,request.Operation.OperationId,request.Operation.ActionHash,at);
        }
        catch(Exception e) when(e is ArgumentException or SecurityConflictException or AgentRunTransitionException) { return saved; }
    }
}
