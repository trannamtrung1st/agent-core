using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Security;
public sealed partial class TenantApprovalService
{
    public async ValueTask<TenantApprovalPreparationResult> PrepareManagementMutationAsync(TenantRunReference reference, string tool, string arguments,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        var permission = TenantManagementBindingRules.IsTool(tool) ? "credentials.bind" : TenantHarnessCommandRules.IsResource(tool) ? "resources.manage" : TenantManagementResourceRules.IsTool(tool) ? TenantManagementResourceRules.Permission(tool)
            : AgentCore.Application.Tools.TenantManagementMutationContract.IsTool(tool) && TenantManagementSkillRules.IsTool(tool) ? "skills.manage" : "agents.update";
        try
        {
            await operations.ResolveForDecisionAsync(reference, permission, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            var target = managementTargets ?? throw new AccessDeniedException("tenant_effect_unavailable");
            var observed = await target.PrepareAsync(reference, generation, tool, arguments, ct).ConfigureAwait(false); var reader = observed.Source.Reader;
            if (observed.Value.Resource is not null) permission = TenantManagementResourceRules.Permission(observed.Value.ToolName);
            if (reader.Run.Revision != expectedRunRevision) throw new SecurityConflictException("stale_revision");
            var op = TenantApprovalRules.PrepareManagementMutation(ids.NewId(), reader.Run.AgentInstanceId, observed.Value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
            var prior = await approvals.FindRootApprovalAsync(reference.TenantId, reader.Run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, op, ct).ConfigureAwait(false);
            if (prior is not null) return await DuplicateOutcome(reader.Security, reader.Run, prior, ct).ConfigureAwait(false);
            var decision = await target.DecisionAsync(observed, ct).ConfigureAwait(false);
            if ((observed.Value.Binding?.Changed == false || observed.Value.Harness?.Changed == false || observed.Value.Skill?.Changed == false || observed.Value.Resource?.Changed == false) && decision.Decision != OperationDecisionKind.Denied)
                return await PreparationOutcome(reader.Security, reader.Run, op, OperationDecisionKind.Allowed, "operation_unchanged", null, ct).ConfigureAwait(false);
            if (decision.Decision != OperationDecisionKind.RequiresApproval)
                return await PreparationOutcome(reader.Security, reader.Run, op, decision.Decision, decision.ReasonCode, null, ct).ConfigureAwait(false);
            var at = time.GetUtcNow(); var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, reader.Run.AgentRunId, reader.Run.SessionId,
                reader.Run.Admission.Activation.RootActivationId, reader.Agent.PrincipalId, reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                decision.ApprovalKind!.Value, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(reader.Agent.Instance.Persona.Name));
            var qualified = false;
            foreach (var human in reader.Security.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantManagementOperationTarget.ReviewerAsync(request, observed, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(reader.Security, reader.Run, op, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
            return await PersistAsync(reference, reader.Run, reader.Security, reader.Agent, op, request.Snapshot, true, expectedRunRevision, generation, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
        {
            await operations.RecordFailureAsync(reference, permission, error is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)error).ReasonCode,
                ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false); throw;
        }
    }
}
