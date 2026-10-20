using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantAccountEffectService
{
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        foreach (var reference in TenantAccountActionReferences.Read(run))
        {
            var tenant = run.Owner.TenantId!.Value;
            var request = await approvals.FindApprovalAsync(tenant, reference.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_source_changed");
            var action = request.Operation.AccountAction ?? throw new AccessDeniedException("account_source_changed");
            var current = await targets.SourceAsync(new(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation), action.Credential.Reference, ct).ConfigureAwait(false);
            targets.Require(current, action, exactSecret: !TenantCredentialOAuthRules.IsOAuth(current.Bound.Credential.Value));
            if (current.Run.Revision != run.Revision || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
                || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("account_source_changed");
            var receipt = await receipts.FindOperationReceiptAsync(tenant, reference.OperationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_source_changed");
            var decision = await receipts.FindApprovalDecisionAsync(tenant, reference.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("account_source_changed");
            var grant = await approvals.FindOperationGrantAsync(tenant, reference.RequestId, ct).ConfigureAwait(false);
            TenantAccountActionReferences.Receipt(reference, request, receipt, decision, grant, run);
            await TenantAccountActionRules.PermissionsAsync(current.State, run.AgentInstanceId, action, current.Agent.PrincipalId, iam, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
            await TenantAccountActionRules.ReviewerAsync(current.State, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
    }
}
