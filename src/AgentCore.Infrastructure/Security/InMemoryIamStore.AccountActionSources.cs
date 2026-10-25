using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireAccountActionSources(AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value;
        foreach (var reference in TenantAccountActionReferences.Read(run))
        {
            var request = _approvals.GetValueOrDefault(reference.RequestId); var receipt = _operationReceipts.GetValueOrDefault(reference.OperationId);
            var decision = _approvalDecisions.GetValueOrDefault(reference.RequestId);
            if (request?.TenantId != tenant || request.Operation.AccountAction is null || receipt is null || decision is null) throw new AccessDeniedException("account_source_changed");
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
            var session = sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
            var bound = ContextCredentialBindings(run).SingleOrDefault(b => b.Binding.BindingId == request.Operation.AccountAction.Credential.BindingId)
                ?? throw new AccessDeniedException("account_source_changed");
            TenantAccountActionRules.Require(request.Operation.AccountAction, run, agent, session, TenantAgentDefinitionBaseline(agent), bound, !TenantCredentialOAuthRules.IsOAuth(bound.Credential.Value));
            if (agent.Instance.Revision != request.Operation.ExpectedInstanceRevision || agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("account_source_changed");
            TenantAccountActionReferences.Receipt(reference, request, receipt, decision, _operationGrants.GetValueOrDefault(reference.RequestId), run);
            TenantAccountActionRules.PermissionsAsync(_tenants[tenant], run.AgentInstanceId, request.Operation.AccountAction, TenantWorkspaceRules.Context(request.Operation), agent.PrincipalId, new IamAuthorizer(this), request.Kind == ApprovalKind.Review, ct).GetAwaiter().GetResult();
            RequireTenantExecutionSourcesAsync(run, _tenants[tenant], new IamAuthorizer(this), this, ct).GetAwaiter().GetResult();
            TenantAccountActionRules.ReviewerAsync(_tenants[tenant], request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        }
    }
    private TenantCredentialBinding RequireAccountActionOperation(TenantApprovalRequest request, AgentRun run, TenantAgent agent,
        AgentRun? source = null, bool exactSecret = true)
    {
        var action = request.Operation.AccountAction ?? throw new SecurityConflictException("operation_mismatch");
        TenantApprovalContinuationRules.Require(request, run, source);
        var session = sessionBacking.ReadTenantSession(request.TenantId, action.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
        var bound = ContextCredentialBindings(run).SingleOrDefault(b => b.Binding.BindingId == action.Credential.BindingId)
            ?? throw new AccessDeniedException("account_source_changed");
        TenantAccountActionRules.Require(action, run, agent, session, TenantAgentDefinitionBaseline(agent), bound, exactSecret);
        TenantAccountActionRules.PermissionsAsync(_tenants[request.TenantId], agent.Instance.InstanceId, action, TenantWorkspaceRules.Context(request.Operation), agent.PrincipalId,
            new IamAuthorizer(this), request.Kind == ApprovalKind.Review, CancellationToken.None).GetAwaiter().GetResult();
        if (request.Snapshot?.Credential != TenantAccountReadRules.Snapshot(agent, bound).Credential) throw new SecurityConflictException("account_source_changed");
        RequireTenantExecutionSourcesAsync(run, _tenants[request.TenantId], new IamAuthorizer(this), this, CancellationToken.None).GetAwaiter().GetResult();
        if (action.ToolName == AgentCore.Application.Tools.ToolCatalog.EmailSend)
            TenantAccountActionRules.RequireDraft(action, agent.Instance.InstanceId, emailDrafts?.GetDraftAsync(action.Draft!.DraftId).Result);
        RequireAccountReadSources(run, CancellationToken.None);
        return bound;
    }
}
