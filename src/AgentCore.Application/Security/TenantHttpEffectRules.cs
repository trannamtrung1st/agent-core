using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantHttpEffectRules
{
    public const string Dispatch="http.request.dispatched",Result="http.request.result",Completed="http_request_completed",Uncertain="http_request_uncertain";
    private static readonly HashSet<string> Denials=new(StringComparer.Ordinal) { "http_request_not_dispatched","http_source_changed","approval_expired","grant_expired","grantor_authority_lost","permission_denied","executor_unavailable","requester_unavailable","tenant_unavailable","policy_transition_pending","provider_unavailable","root_operation_conflict","operation_witness_changed","resource_unavailable","independent_reviewer_required","approver_unqualified","execution_claim_expired","grant_unavailable" };
    public static string Denial(string reason)=>Denials.Contains(reason)?reason:"http_source_changed";
    public static DateTimeOffset Milliseconds(DateTimeOffset at)=>DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds());
    public static void Start(TenantApprovalRequest request,TenantApprovalDecision decision,TenantOperationGrant? grant,AgentRun run,TenantOperationCommit commit,AgentRun? source)
    {
        TenantLocalEffectRules.ValidateApproval(request,decision,grant,run,commit,source); RequireGrant(request,grant,OperationGrantStatus.Available);
        if(request.Operation.HttpRequest is null || commit.Outcome is not (TenantOperationOutcome.Completed or TenantOperationOutcome.Denied)
            || (commit.Outcome==TenantOperationOutcome.Completed?commit.ReasonCode!="operation_completed":!Denials.Contains(commit.ReasonCode)))throw new SecurityConflictException("operation_mismatch");
    }
    public static void RequireGrant(TenantApprovalRequest request,TenantOperationGrant? grant,OperationGrantStatus status)
    {
        if(request.Operation.HttpRequest is null || !Enum.IsDefined(request.Kind))throw new SecurityConflictException("grant_mismatch");
        if(request.Kind!=ApprovalKind.ExceptionalGrant) { if(grant is not null)throw new SecurityConflictException("grant_mismatch");return; }
        var permission=PermissionCatalog.Get(request.Permission);
        if(request.Permission!="http.request" || !permission.Delegatable || permission.HumanOnly || permission.SystemOnly || grant is null || grant.Status!=status
            || grant.TenantId!=request.TenantId || grant.RequestId!=request.RequestId || grant.ExecutorPrincipalId!=request.ExecutorPrincipalId || grant.GrantId==Guid.Empty || grant.Revision<1
            || grant.OperationId!=request.Operation.OperationId || grant.ActionHash!=request.Operation.ActionHash || Milliseconds(grant.ExpiresAt)!=Milliseconds(request.ExpiresAt)
            || grant.EffectIdempotencyKey!="operation:"+request.Operation.OperationId.ToString("D"))throw new SecurityConflictException("grant_unavailable");
    }
    public static async ValueTask AuthorityAsync(TenantSecurityState state,TenantApprovalRequest request,TenantApprovalDecision decision,TenantAgent agent,AgentRun run,
        IamAuthorizer iam,TenantOperationGrant? grant,OperationGrantStatus status,DateTimeOffset at,CancellationToken ct,bool stored=false)
    {
        if(state.Tenant.TenantId!=request.TenantId || state.Tenant.Status!=IdentityStatus.Active)throw new AccessDeniedException("tenant_unavailable");
        if(request.Status!=TenantApprovalStatus.Approved || decision.Status!=TenantApprovalStatus.Approved || decision.RequestId!=request.RequestId || decision.TenantId!=request.TenantId
            || decision.ActionHash!=request.Operation.ActionHash || decision.RequestRevision!=request.Revision)throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request,grant,status);
        if(!stored && request.ExpiresAt<=at)throw new AccessDeniedException("approval_expired");
        if(!stored && grant?.ExpiresAt<=at)throw new AccessDeniedException("grant_expired");
        await TenantHttpOperationRules.PermissionsAsync(state,run,agent,iam,request.Kind==ApprovalKind.Review,ct).ConfigureAwait(false);
        try { await TenantHttpOperationRules.ReviewerAsync(state,request,decision.HumanPrincipalId,iam,ct).ConfigureAwait(false); }
        catch(AccessDeniedException) when(request.Kind==ApprovalKind.ExceptionalGrant) { throw new AccessDeniedException("grantor_authority_lost"); }
    }
    public static TenantHttpDispatch Fence(TenantApprovalRequest request,AgentRun run)=>new(request.TenantId,request.RequestId,request.Operation.OperationId,run.AgentRunId,request.Operation.ActionHash,
        run.SideEffect.ExternalEffect ?? throw new SecurityConflictException("http_effect_fence_required"));
    public static void RequireFence(TenantHttpDispatch fence,TenantApprovalRequest request,AgentRun run,AgentRun? source,TenantOperationGrant? grant)
    {
        TenantRunContinuationRules.Require(run,request,source);
        if(request.Operation.HttpRequest is null || request.Status!=TenantApprovalStatus.Approved || fence!=Fence(request,run)
            || run.SideEffect.ToolCallId!="operation:"+fence.OperationId.ToString("D") || run.SideEffect.ActionHash!=fence.ActionHash || run.Revision<fence.Origin.StartedRunRevision
            || run.SideEffect.Disposition is not (AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate))throw new SecurityConflictException("operation_mismatch");
        RequireGrant(request,grant,OperationGrantStatus.Claimed);
    }
    public static bool Recoverable(AgentRun run,DateTimeOffset at)=>run.SideEffect.Disposition==AgentRunSideEffectDisposition.Indeterminate || run.Status!=AgentRunStatus.Running || run.Claim is null
        || run.Claim.Generation!=run.SideEffect.ExternalEffect?.Generation || run.Claim.LeaseExpiresAtUtc<=at;
    public static void Transaction(TenantSecurityState next,long revision,TenantApprovalRequest request,AuthorizationAudit audit,bool dispatch)
    {
        if(next.Tenant.TenantId!=request.TenantId || next.Tenant.Revision!=checked(revision+1) || audit.TenantId!=request.TenantId || audit.ActorPrincipalId!=request.ExecutorPrincipalId
            || audit.Target!=request.ResourceId || audit.Action!=(dispatch?Dispatch:Result))throw new ArgumentException("Invalid exact HTTP effect transaction.");
    }
    public static void Outcome(TenantOperationOutcome outcome,string reason,bool recovery)
    {
        if(recovery?outcome!=TenantOperationOutcome.Indeterminate || reason!=Uncertain:outcome switch {
            TenantOperationOutcome.Completed=>reason!=Completed,TenantOperationOutcome.Denied=>!Denials.Contains(reason),TenantOperationOutcome.Indeterminate=>reason!=Uncertain,_=>true })throw new ArgumentException("Invalid factual HTTP outcome.");
    }
    public static AgentRun SettleRun(AgentRun run,TenantHttpDispatch fence,TenantOperationOutcome outcome,DateTimeOffset at)=>run.Status!=AgentRunStatus.Running || run.CancellationRequested
        || run.Claim?.Generation!=fence.Origin.Generation || run.SideEffect.Disposition!=AgentRunSideEffectDisposition.InFlight?run:
        run.MarkSideEffect(run.Revision,fence.Origin.Generation,outcome switch { TenantOperationOutcome.Completed=>AgentRunSideEffectDisposition.Succeeded,TenantOperationOutcome.Denied=>AgentRunSideEffectDisposition.DefinitelyFailed,_=>AgentRunSideEffectDisposition.Indeterminate },"operation:"+fence.OperationId.ToString("D"),fence.ActionHash,at);
    public static TenantOperationReceipt Receipt(TenantApprovalRequest request,AgentRun run,TenantOperationOutcome outcome,string reason,DateTimeOffset at,TenantHttpReceipt? fact=null)
    {
        if((fact is not null)!=(outcome==TenantOperationOutcome.Completed))throw new ArgumentException("HTTP completion requires factual response metadata.");
        if(fact is not null) { Validate(fact);if(outcome!=TenantOperationOutcome.Completed)throw new ArgumentException("Only completed HTTP observations retain metadata."); }
        return new(request.Operation.OperationId,request.RequestId,request.TenantId,run.AgentRunId,run.SessionId,request.RootActivationId,request.ExecutorPrincipalId,request.Operation.InstanceId,
            request.Operation.ActionHash,outcome,reason,request.Operation.ExpectedInstanceRevision,request.Operation.ExpectedPersonaRevision,Milliseconds(at),Http:fact);
    }
    public static TenantOperationReceipt Matching(TenantOperationReceipt receipt,TenantHttpDispatch fence)
    {
        if(receipt.TenantId!=fence.TenantId || receipt.RequestId!=fence.RequestId || receipt.OperationId!=fence.OperationId || receipt.RunId!=fence.RunId || receipt.ActionHash!=fence.ActionHash
            || receipt.SourceCallId is not null || receipt.ParametersHash is not null)throw new SecurityConflictException("operation_mismatch");
        return receipt;
    }
    public static TenantHttpResult Normalize(HttpToolResponse response)
    {
        if(response.StatusCode is <100 or >599 || response.ErrorCode is not null)throw new ArgumentException("HTTP response is unavailable.");
        var url=TenantPublicWebReadContract.PublicUrl(response.FinalUrl).AbsoluteUri; var body=ToolJsonResults.ClipUtf8Prefix(response.Body,65536);
        return new(response.StatusCode,url,response.ContentType is { Length:<=200 } type?type:null,body,response.Truncated || body!=response.Body);
    }
    public static TenantHttpReceipt Metadata(TenantHttpResult result)=>new(result.StatusCode,result.FinalUrl,result.ContentType,Encoding.UTF8.GetByteCount(result.Body),
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.Body))),result.Truncated);
    public static void Validate(TenantHttpReceipt fact)
    {
        if(fact.StatusCode is <100 or >599 || TenantPublicWebReadContract.PublicUrl(fact.FinalUrl).AbsoluteUri!=fact.FinalUrl || fact.ContentType?.Length>200
            || fact.ProjectedBodyBytes is <0 or >65536 || fact.BodySha256 is not { Length:64 } || fact.BodySha256.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))throw new ArgumentException("Invalid HTTP receipt.");
    }
}
