using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareSandboxControlAsync(TenantRunReference reference, SandboxControlKind kind, Guid? executionId,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try
        {
            if (!Enum.IsDefined(kind) || kind == SandboxControlKind.StopProcess && executionId is null || executionId == Guid.Empty
                || kind == SandboxControlKind.ResetEnvironment && executionId is not null) throw new ArgumentException("Invalid Sandbox control intent.");
            var (run, state) = await operations.ResolveForDecisionAsync(reference, "sandbox.manage", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            if (run.Status != AgentRunStatus.Running || run.Claim?.Generation != generation || run.Revision != expectedRunRevision || run.Claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                throw new AccessDeniedException("tenant_runtime_unavailable");
            var owner = controlTargets ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var target = await owner.ResolveAsync(run, kind, executionId, ct).ConfigureAwait(false);
            var value = TenantSandboxControlOperationRules.Parameters(kind, target.Session, target.Lease, target.Execution);
            var operation = TenantApprovalRules.PrepareSandboxControl(ids.NewId(), run.AgentInstanceId, value, target.Agent.Instance.Revision, target.Agent.Instance.PersonaRevision);
            var scope = AccessScope.ForResource(reference.TenantId, "agent", operation.ResourceId);
            var eligible = false;
            foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                if (!await operations.QualifiesAsync(state, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, human.PrincipalId, PermissionCatalog.Get("sandbox.manage"), scope, ApprovalKind.Review, ct).ConfigureAwait(false)) continue;
                try { await TenantSandboxControlOperationRules.RequirePermissionsAsync(state, target.Session.Owner, value.ProfileId, human.PrincipalId, authorizer, ct).ConfigureAwait(false); eligible = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!eligible) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, run, state, target.Agent, operation,
                TenantSandboxControlOperationRules.Snapshot(target.Agent, target.Lease, target.Execution), true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, "sandbox.manage", e is AccessDeniedException d ? d.ReasonCode : ((SecurityConflictException)e).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }
}
