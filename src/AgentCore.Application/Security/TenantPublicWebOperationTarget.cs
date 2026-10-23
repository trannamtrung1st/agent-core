using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantPublicWebSource(AgentRun Run,TenantSession Session,TenantAgent Agent,TenantSecurityState State,AgentDefinition Definition);

// Shared current source boundary for existing public research and exact-approved HTTP.
public sealed class TenantPublicWebOperationTarget(TenantOperationAuthorizer operations,ITenantRunStore runs,ITenantSessionStore sessions,
    TenantRunConfigurationResolver configuration,IamAuthorizer iam,IIamStore security,TimeProvider time,IToolConfigurationGate? tools=null)
{
    public async ValueTask<TenantPublicWebSource> RequireAsync(TenantRuntimeFrame frame,string tool,CancellationToken ct,bool audit=false,bool allowReview=false)
    {
        var (run,state)=await operations.ResolveAsync(new(frame.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required"),frame.RunId),ct).ConfigureAwait(false);
        var session=await sessions.FindTenantSessionAsync(state.Tenant.TenantId,frame.SessionId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantRuntimeRules.Require(frame,session.Owner,run,time.GetUtcNow());
        var current=await configuration.CurrentPolicyAsync(state.Tenant.TenantId,run.AgentInstanceId,ct).ConfigureAwait(false);
        if(current.Agent.PrincipalId!=frame.Owner.ExecutorPrincipalId || !TenantTaskExecutionContract.Allows(run,current.Definition)
            || !Allows(run.Admission.Configuration!.Definition,tool) || !Allows(current.Definition,tool)
            || current.Agent.Instance.Lifecycle!=AgentInstanceLifecycle.Active || session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus!=SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended)
            throw new AccessDeniedException("public_web_unavailable");
        if(tools?.IsConfigured(tool)==false)throw new AccessDeniedException("provider_unavailable");
        foreach(var (permission,scope) in TenantPublicWebReadRules.Targets(run))
        {
            if(!(await iam.CheckOwnedTenantAsync(state,current.Agent.PrincipalId,PermissionCatalog.Get(permission),scope,ct).ConfigureAwait(false)).Allowed)throw new AccessDeniedException("executor_read_required");
            var decision=audit ? await operations.Check(state,run,permission,scope,ct,purpose:ExecutionAuthorizationPurpose.ToolBoundary).ConfigureAwait(false)
                : await operations.Evaluate(state,run.Admission.ExecutionContext!,permission,scope,ct).ConfigureAwait(false);
            if(decision.Decision!=OperationDecisionKind.Allowed && !(allowReview && decision.Decision==OperationDecisionKind.RequiresApproval))
                throw new AccessDeniedException(decision.Decision==OperationDecisionKind.RequiresApproval?"public_web_read_review_required":decision.ReasonCode);
        }
        return new(run,session,current.Agent,state,current.Definition);
    }
    public async ValueTask RequireSavedAsync(TenantApprovalRequest request,AgentRun run,CancellationToken ct)
    {
        var current=await configuration.CurrentPolicyAsync(request.TenantId,run.AgentInstanceId,ct).ConfigureAwait(false);
        var session=await sessions.FindTenantSessionAsync(request.TenantId,run.SessionId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var state=await security.ReadTenantAsync(request.TenantId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        TenantApprovalRules.Validate(request,run,current.Agent);
        TenantHttpOperationRules.Require(request.Operation.HttpRequest!,run,current.Agent,current.Definition,session);
        await TenantHttpOperationRules.PermissionsAsync(state,run,current.Agent,iam,request.Kind==ApprovalKind.Review,ct).ConfigureAwait(false);
        if(tools?.IsConfigured(ToolCatalog.HttpRequest)==false)throw new AccessDeniedException("provider_unavailable");
    }
    public async ValueTask FenceAsync(TenantRuntimeFrame frame,string tool,TenantPublicWebSource before,CancellationToken ct)
    {
        var after=await RequireAsync(frame,tool,ct).ConfigureAwait(false);
        if(before.Run.Revision!=after.Run.Revision || before.State.Tenant.Revision!=after.State.Tenant.Revision
            || before.Agent.Instance.Revision!=after.Agent.Instance.Revision || before.Session.Owner!=after.Session.Owner || before.Session.Snapshot.Revision!=after.Session.Snapshot.Revision
            || (await security.ReadTenantAsync(after.State.Tenant.TenantId,ct).ConfigureAwait(false))?.Tenant.Revision!=after.State.Tenant.Revision)throw new SecurityConflictException("public_web_source_changed");
        var final=await runs.FindTenantRunAsync(after.State.Tenant.TenantId,frame.RunId,ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        TenantRuntimeRules.Require(frame,after.Session.Owner,final,time.GetUtcNow());
        if(final.Revision!=after.Run.Revision)throw new SecurityConflictException("public_web_source_changed");
    }
    public ValueTask RecordFailureAsync(TenantRuntimeFrame frame,string permission,string reason,CancellationToken ct)=>frame.Owner.TenantId is { } tenant
        ? operations.RecordFailureAsync(new(tenant,frame.RunId),permission,reason,ExecutionAuthorizationPurpose.ToolBoundary,ct,frame.SessionId) : ValueTask.CompletedTask;
    public static bool Allows(AgentDefinition definition,string tool)=>TenantPublicWebReadContract.IsRead(tool)?TenantPublicWebReadContract.Allows(definition,tool):tool==ToolCatalog.HttpRequest && TenantHttpRequestContract.Allows(definition);
}
