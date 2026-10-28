using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Workflows;

namespace AgentCore.Application.Workflows;

public static class WorkflowDecisionRules
{
    public static string Fingerprint(WorkflowDecisionCommand command)
    {
        _ = WorkflowCatalogRules.RequestKey(command.RequestKey);
        if (command.Rationale is { Length: > 2000 }) throw new ArgumentException("A decision rationale is limited to 2000 characters.");
        return WorkflowExecutionRules.Hash(JsonSerializer.Serialize(new { command.TenantId, command.ExecutionId, command.NodeId,
            command.Human.PrincipalId, command.Human.AccountId, command.Approved, command.Rationale }));
    }
    public static async ValueTask<WorkflowProcessDecision> RequireAsync(WorkflowDecisionCommand command, TenantSecurityState state,
        WorkflowExecution execution, WorkflowVersion version, WorkflowStepOperation operation, IamAuthorizer iam, DateTimeOffset now, CancellationToken ct)
    {
        var binding = version.Graph.Read().Nodes.Single(n => n.Id == command.NodeId).Decision;
        if (binding is null || execution.SourceClosure is not null || execution.CancellationRequested || WorkflowExecutionRules.Terminal(execution.Status)
            || execution.DeadlineAt <= now || operation.CreatedAt.AddHours(binding.TimeoutHours) <= now
            || operation.Kind != WorkflowNodeKind.HumanDecision || operation.Status != WorkflowOperationStatus.Reserved
            || binding.ReviewerPrincipalId != command.Human.PrincipalId
            || !state.Users.Any(u => u.PrincipalId == command.Human.PrincipalId && u.AccountId == command.Human.AccountId && u.Status == IdentityStatus.Active))
            throw new AccessDeniedException("workflow_decision_unavailable");
        if (!(await iam.CheckOwnedTenantAsync(state, command.Human.PrincipalId, PermissionCatalog.Get("workflows.decide"),
            WorkflowCatalogAuthority.Scope(command.TenantId, execution.WorkflowId), ct)).Allowed) throw new AccessDeniedException("permission_denied");
        if (execution.Revision != command.ExpectedExecutionRevision || operation.Revision != command.ExpectedStepRevision
            || state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        return new(command.TenantId, command.ExecutionId, command.NodeId, operation.OperationId, execution.GraphHash,
            command.Human.PrincipalId, command.Human.AccountId, command.Approved, command.Rationale, command.RequestKey, Fingerprint(command), now);
    }
    public static WorkflowStepOperation Settle(WorkflowStepOperation step, WorkflowProcessDecision receipt) => step with {
        Revision = step.Revision + 1, Status = WorkflowOperationStatus.Settled, Outcome = receipt.Approved ? "approve" : "reject",
        DecisionActorId = receipt.ReviewerPrincipalId, ProofAccepted = receipt.Approved, SettledAt = receipt.At
    };
}
