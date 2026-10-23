using AgentCore.Application.Ports;
using AgentCore.Application.Agents;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireHttpSources(AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value;
        foreach (var reference in TenantHttpReferences.Read(run))
        {
            var request = _approvals.GetValueOrDefault(reference.RequestId); var receipt = _operationReceipts.GetValueOrDefault(reference.OperationId);
            var decision = _approvalDecisions.GetValueOrDefault(reference.RequestId);
            if (request?.TenantId != tenant || request.Operation.HttpRequest is null || receipt is null || decision is null) throw new AccessDeniedException("http_source_changed");
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var session = sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            TenantHttpOperationRules.Require(request.Operation.HttpRequest, run, agent, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent),agent.Instance.SettingsOverrides), session);
            if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("http_source_changed");
            TenantHttpReferences.Receipt(reference, request, receipt, decision, _operationGrants.GetValueOrDefault(reference.RequestId), run);
            TenantHttpOperationRules.PermissionsAsync(_tenants[tenant], run, agent, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).GetAwaiter().GetResult();
            RequireTenantExecutionSourcesAsync(run, _tenants[tenant], new IamAuthorizer(this), this, ct).GetAwaiter().GetResult();
            TenantHttpOperationRules.ReviewerAsync(_tenants[tenant], request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        }
    }
    private void RequireHttpOperation(TenantApprovalRequest request,AgentRun run,TenantAgent agent,AgentRun? source=null)
    {
        TenantApprovalRules.Validate(request,run,agent,source:source);
        var value=request.Operation.HttpRequest ?? throw new SecurityConflictException("operation_mismatch");
        var session=sessionBacking.ReadTenantSession(request.TenantId,value.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        TenantHttpOperationRules.Require(value,run,agent,InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent),agent.Instance.SettingsOverrides),session);
        TenantHttpOperationRules.PermissionsAsync(_tenants[request.TenantId],run,agent,new IamAuthorizer(this),request.Kind==ApprovalKind.Review,CancellationToken.None).GetAwaiter().GetResult();
        RequireTenantExecutionSourcesAsync(run,_tenants[request.TenantId],new IamAuthorizer(this),this,CancellationToken.None).GetAwaiter().GetResult();
    }
}
