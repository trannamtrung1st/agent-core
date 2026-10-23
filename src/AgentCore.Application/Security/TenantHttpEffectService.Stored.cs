using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantHttpEffectService
{
    public async ValueTask RequireStoredAsync(AgentRun run, CancellationToken ct = default)
    {
        foreach (var reference in TenantHttpReferences.Read(run))
        {
            var tenant = run.Owner.TenantId!.Value;
            var request = await approvals.FindApprovalAsync(tenant, reference.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("http_source_changed");
            var action = request.Operation.HttpRequest ?? throw new AccessDeniedException("http_source_changed");
            var current=await targets.RequireAsync(new(run.Owner,run.SessionId,run.AgentRunId,run.Claim!.Generation),AgentCore.Application.Tools.ToolCatalog.HttpRequest,ct,allowReview:true).ConfigureAwait(false);
            if (current.Run.Revision != run.Revision || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
                || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision) throw new AccessDeniedException("http_source_changed");
            var receipt = await receipts.FindOperationReceiptAsync(tenant, reference.OperationId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("http_source_changed");
            var decision = await receipts.FindApprovalDecisionAsync(tenant, reference.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("http_source_changed");
            var grant = await approvals.FindOperationGrantAsync(tenant, reference.RequestId, ct).ConfigureAwait(false);
            TenantHttpReferences.Receipt(reference, request, receipt, decision, grant, run);
            await TenantHttpOperationRules.PermissionsAsync(current.State, run, current.Agent, iam, request.Kind == ApprovalKind.Review, ct).ConfigureAwait(false);
            await TenantHttpOperationRules.ReviewerAsync(current.State, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
        }
    }
}
