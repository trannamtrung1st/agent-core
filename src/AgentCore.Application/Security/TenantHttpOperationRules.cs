using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantHttpOperationRules
{
    public static void Require(PreparedHttpRequest value,AgentRun run,TenantAgent agent,AgentDefinition current,TenantSession session)
    {
        _=TenantHttpRequestContract.Arguments(value);
        if(run.Owner.TenantId!=agent.TenantId || run.Owner.ExecutorPrincipalId!=agent.PrincipalId || run.AgentInstanceId!=agent.Instance.InstanceId || value.SessionId!=run.SessionId
            || session.Owner.TenantId!=agent.TenantId || session.Owner.SessionId!=value.SessionId || session.Owner.AgentInstanceId!=run.AgentInstanceId || session.Owner.ExecutorPrincipalId!=agent.PrincipalId
            || agent.Instance.Lifecycle!=AgentInstanceLifecycle.Active || !TenantTaskExecutionContract.Allows(run,current) || !TenantHttpRequestContract.Allows(current)
            || !TenantHttpRequestContract.Allows(run.Admission.Configuration!.Definition) || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus is not (SessionLifecycleStatus.Active or SessionLifecycleStatus.Completed) || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("http_source_changed");
    }
    public static (string Permission,AccessScope Scope)[] Targets(Guid tenant,Guid instance,Guid session)=>[
        ("http.request",AccessScope.ForResource(tenant,"agent",instance.ToString("D"))),
        ("agents.read",AccessScope.ForResource(tenant,"agent",instance.ToString("D"))), ("sessions.read",TenantSessionRules.Scope(tenant,session))];
    public static async ValueTask PermissionsAsync(TenantSecurityState state,AgentRun run,TenantAgent agent,IamAuthorizer iam,bool primary,CancellationToken ct)
    {
        TenantSessionRules.RequireExecutor(state,agent);
        foreach(var (key,scope) in Targets(state.Tenant.TenantId,run.AgentInstanceId,run.SessionId))
            if((primary || key!="http.request") && !(await iam.CheckOwnedTenantAsync(state,agent.PrincipalId,PermissionCatalog.Get(key),scope,ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(key=="http.request"?"permission_denied":"executor_read_required");
    }
    public static async ValueTask ReviewerAsync(TenantSecurityState state,TenantApprovalRequest request,Guid human,IamAuthorizer iam,CancellationToken ct)
    {
        if(request.Operation.HttpRequest is null)throw new AccessDeniedException("operation_mismatch");
        foreach(var (key,scope) in Targets(state.Tenant.TenantId,request.Operation.InstanceId,request.SessionId))
        {
            var permission=PermissionCatalog.Get(key);
            if(!(await iam.CheckTenantAsync(state,human,permission,scope,ct).ConfigureAwait(false)).Allowed)throw new AccessDeniedException("approver_unqualified");
            var policy=state.OperationPolicies.SingleOrDefault(p=>p.PermissionKey==key);
            if(policy?.IndependentReview==true && request.OriginalInitiatorPrincipalId==human)throw new AccessDeniedException("independent_reviewer_required");
        }
    }
}
