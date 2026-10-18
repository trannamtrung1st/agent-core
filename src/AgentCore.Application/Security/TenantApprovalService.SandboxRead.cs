using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareSandboxReadAsync(TenantRunReference reference, SandboxReadKind kind, Guid? executionId,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            var (run, _) = await operations.ResolveForDecisionAsync(reference, "sandbox.read", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                throw new AccessDeniedException("tenant_runtime_unavailable");
            var reads = sandboxReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var prepared = await reads.PrepareInspectionAsync(new(run.Owner, run.SessionId, run.AgentRunId, generation), kind, executionId, ct).ConfigureAwait(false);
            if (prepared.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var operation = TenantApprovalRules.PrepareSandboxRead(ids.NewId(), run.AgentInstanceId, prepared.Read, prepared.Agent.Instance.Revision, prepared.Agent.Instance.PersonaRevision);
            if (!prepared.ReviewRequired) return await PreparationOutcome(prepared.Security, prepared.Run, operation, OperationDecisionKind.Allowed, "allowed", null, ct).ConfigureAwait(false);
            var qualified = false;
            foreach (var human in prepared.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try
                {
                    await TenantSandboxReadOperationRules.RequireReviewerAsync(prepared.Security, prepared.Owner, prepared.Read,
                        prepared.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
                    qualified = true; break;
                }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(prepared.Security, prepared.Run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, prepared.Run, prepared.Security, prepared.Agent, operation, prepared.Snapshot, true,
                expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "sandbox.read", e is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
}
