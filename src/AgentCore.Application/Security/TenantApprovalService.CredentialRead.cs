using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareCredentialReadAsync(TenantRunReference reference, string arguments,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var (run, _) = await operations.ResolveForDecisionAsync(reference, "credentials.read", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                throw new AccessDeniedException("tenant_runtime_unavailable");
            var prepared = await (credentialReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareInspectionAsync(
                new(run.Owner, run.SessionId, run.AgentRunId, generation), arguments, ct).ConfigureAwait(false);
            var op = TenantApprovalRules.PrepareCredentialRead(ids.NewId(), run.AgentInstanceId, prepared.Read, prepared.Agent.Instance.Revision, prepared.Agent.Instance.PersonaRevision);
            if (!prepared.ReviewRequired) return await PreparationOutcome(prepared.State, run, op, OperationDecisionKind.Allowed, "allowed", null, ct).ConfigureAwait(false);
            var qualified = false;
            foreach (var human in prepared.State.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantCredentialReadOperationRules.ReviewerAsync(prepared.State, prepared.Read, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                    human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(prepared.State, run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, prepared.State, prepared.Agent, op, prepared.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "credentials.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
