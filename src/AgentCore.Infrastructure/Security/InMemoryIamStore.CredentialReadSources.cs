using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireCredentialReadSources(AgentRun run, CancellationToken ct)
    {
        try
        {
            foreach (var source in TenantCredentialReadReferences.Read(run))
            {
                var tenant = run.Owner.TenantId!.Value; var state = _tenants[tenant]; var iam = new IamAuthorizer(this);
                var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
                TenantSessionRules.RequireExecutor(state, agent);
                RequireTenantExecutionSourcesAsync(run, state, iam, this, ct).GetAwaiter().GetResult();
                if (source.RequestId is { } requestId)
                {
                    var request = _approvals.GetValueOrDefault(requestId); var receipt = _operationReceipts.GetValueOrDefault(source.OperationId!.Value);
                    var decision = _approvalDecisions.GetValueOrDefault(requestId);
                    if (request?.TenantId != tenant || request.Operation.CredentialRead is null || receipt is null || decision is null || _operationGrants.ContainsKey(requestId))
                        throw new AccessDeniedException("credential_source_changed");
                    TenantCredentialReadReferences.Receipt(source, request, receipt, decision, run);
                    RequireCredentialReadOperation(request, run, agent);
                    TenantCredentialReadOperationRules.ReviewerAsync(state, request.Operation.CredentialRead, request.OriginalInitiatorPrincipalId, decision.HumanPrincipalId, iam, ct).GetAwaiter().GetResult();
                }
                else
                {
                    var session = sessionBacking.ReadTenantSession(tenant, run.SessionId) ?? throw new AccessDeniedException("resource_unavailable");
                    var bindings = ContextCredentialBindings(run, ct);
                    var read = TenantCredentialReadReferences.Direct(run, source, bindings);
                    TenantCredentialReadOperationRules.Require(read, run, agent, session, TenantAgentDefinitionBaseline(agent), bindings);
                    TenantCredentialReadOperationRules.PermissionsAsync(state, read, agent.PrincipalId, iam, ct).GetAwaiter().GetResult();
                    foreach (var (permission, scope) in TenantCredentialReadOperationRules.Targets(tenant, read))
                        if (TenantSandboxReadOperationRules.NeedsReviewAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, iam, ct).Result)
                            throw new AccessDeniedException("credential_read_review_required");
                }
            }
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException)
        { throw new AccessDeniedException("account_metadata_unavailable"); }
    }
}
