using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareHttpRequestAsync(TenantRunReference reference,string arguments,long expectedRunRevision,Guid generation,CancellationToken ct=default)
    {
        try
        {
            var (run,_)=await operations.ResolveForDecisionAsync(reference,"http.request",ExecutionAuthorizationPurpose.ApprovalPreparation,ct).ConfigureAwait(false);
            if(run.Revision!=expectedRunRevision || run.Claim?.Generation!=generation)throw new AccessDeniedException("tenant_runtime_unavailable");
            var args=TenantHttpRequestContract.Parse(arguments);
            var source=await (webTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(new(run.Owner,run.SessionId,run.AgentRunId,generation),ToolCatalog.HttpRequest,ct,allowReview:true).ConfigureAwait(false);
            var value=TenantHttpRequestContract.Parameters(run.SessionId,args);
            var op=TenantApprovalRules.PrepareHttpRequest(ids.NewId(),run.AgentInstanceId,value,source.Agent.Instance.Revision,source.Agent.Instance.PersonaRevision);
            var prior=await approvals.FindRootApprovalAsync(reference.TenantId,run.Admission.ExecutionContext!.RootActivationId,source.Agent.PrincipalId,op,ct).ConfigureAwait(false);
            if(prior is not null)return await DuplicateOutcome(source.State,run,prior,ct).ConfigureAwait(false);
            var decision=await operations.Evaluate(source.State,run.Admission.ExecutionContext!,op.Permission,TenantApprovalRules.Scope(reference.TenantId,op),ct).ConfigureAwait(false);
            if(decision.Decision!=OperationDecisionKind.RequiresApproval)return await PreparationOutcome(source.State,run,op,decision.Decision,decision.ReasonCode,null,ct).ConfigureAwait(false);
            var request=new TenantApprovalRequest(ids.NewId(),reference.TenantId,run.AgentRunId,run.SessionId,run.Admission.ExecutionContext.RootActivationId,source.Agent.PrincipalId,
                run.Admission.ExecutionContext.OriginalInitiatorPrincipalId,decision.ApprovalKind!.Value,op,TenantApprovalStatus.Pending,1,time.GetUtcNow(),time.GetUtcNow().AddHours(1),new(source.Agent.Instance.Persona.Name));
            await TenantHttpOperationRules.PermissionsAsync(source.State,run,source.Agent,authorizer,request.Kind==ApprovalKind.Review,ct).ConfigureAwait(false);
            var qualified=false;
            foreach(var human in source.State.Users.Where(u=>u.Status==IdentityStatus.Active))
            {
                try { await TenantHttpOperationRules.ReviewerAsync(source.State,request,human.PrincipalId,authorizer,ct).ConfigureAwait(false); qualified=true;break; }
                catch(AccessDeniedException) { }
            }
            if(!qualified)return await PreparationOutcome(source.State,run,op,OperationDecisionKind.Denied,"no_eligible_approver",null,ct).ConfigureAwait(false);
            return await PersistAsync(reference,run,source.State,source.Agent,op,request.Snapshot,false,expectedRunRevision,generation,ct).ConfigureAwait(false);
        }
        catch(Exception e) when(e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference,"http.request",e is AccessDeniedException denied?denied.ReasonCode:((SecurityConflictException)e).ReasonCode,ExecutionAuthorizationPurpose.ApprovalPreparation,ct).ConfigureAwait(false);throw;
        }
    }
}
