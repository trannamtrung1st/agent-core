using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Ports;

public sealed record HumanWorkflowStart(HumanSessionReference Human, WorkflowExecution Execution, long ExpectedSecurityRevision, Guid AuditId);
public sealed record ClaimedWorkflowStart(TenantRuntimeFrame Frame, WorkflowExecution Execution, long ExpectedSecurityRevision, Guid AuditId);
public sealed record WorkflowExecutionWrite(WorkflowExecution Execution, long ExpectedRevision, Guid AuditId);
public sealed record WorkflowCancelCommand(Guid TenantId, Guid ExecutionId, HumanSessionReference Human,
    long ExpectedRevision, long ExpectedSecurityRevision, string RequestKey, Guid AuditId);
public sealed record WorkflowStepReservation(Guid TenantId, Guid ExecutionId, WorkflowStepOperation Operation, long ExpectedExecutionRevision, Guid AuditId);
public sealed record WorkflowStepWrite(WorkflowStepOperation Operation, long ExpectedRevision, Guid AuditId);
public sealed record WorkflowTaskAdmission(Guid TenantId, Guid ExecutionId, Guid OperationId, long ExpectedOperationRevision,
    long ExpectedSecurityRevision, DelegatedTask Task, SessionSnapshot Session, AgentRun Run, Guid AuditId);
public sealed record WorkflowDecisionCommand(Guid TenantId, Guid ExecutionId, string NodeId, HumanSessionReference Human,
    bool Approved, string? Rationale, string RequestKey, long ExpectedExecutionRevision, long ExpectedStepRevision,
    long ExpectedSecurityRevision, Guid AuditId);

public interface ITenantWorkflowExecutionStore
{
    ValueTask<WorkflowExecution?> FindWorkflowExecutionAsync(Guid tenant, Guid execution, CancellationToken ct = default);
    ValueTask<WorkflowExecution?> FindWorkflowStartRequestAsync(Guid tenant, string scope, string key, CancellationToken ct = default);
    ValueTask<WorkflowPage<WorkflowExecution>> ListWorkflowExecutionsAsync(Guid tenant, IReadOnlyList<Guid>? allowedWorkflows, int page, int size, CancellationToken ct = default, Guid? reader = null);
    ValueTask<WorkflowExecution> StartHumanWorkflowAsync(HumanWorkflowStart command, CancellationToken ct = default);
    ValueTask<bool> WriteWorkflowExecutionAsync(WorkflowExecutionWrite command, CancellationToken ct = default);
    ValueTask<IReadOnlyList<WorkflowStepOperation>> ReadWorkflowStepsAsync(Guid tenant, Guid execution, CancellationToken ct = default);
    ValueTask<IReadOnlyList<DelegatedTask>> ReadWorkflowTasksAsync(Guid tenant, Guid execution, CancellationToken ct = default);
    ValueTask<WorkflowStepOperation> ReserveWorkflowStepAsync(WorkflowStepReservation command, CancellationToken ct = default);
    ValueTask<bool> WriteWorkflowStepAsync(WorkflowStepWrite command, CancellationToken ct = default);
    ValueTask<IReadOnlyList<WorkflowExecution>> ListWorkflowRecoveryAsync(int limit, CancellationToken ct = default);
    ValueTask<WorkflowProcessDecision?> FindWorkflowDecisionAsync(Guid tenant, Guid execution, string node, CancellationToken ct = default);
    ValueTask<WorkflowProcessDecision> DecideWorkflowAsync(WorkflowDecisionCommand command, CancellationToken ct = default);
    ValueTask<WorkflowExecution> CancelWorkflowAsync(WorkflowCancelCommand command, CancellationToken ct = default);
}
public interface ITenantWorkflowTaskStore
{
    ValueTask<TaskMutationResult> AdmitWorkflowTaskAsync(WorkflowTaskAdmission command, CancellationToken ct = default);
}
public sealed record AutomationWorkflowAdmission(AgentCore.Domain.Triggers.Automation Current, AgentCore.Domain.Triggers.Automation Next,
    AgentCore.Domain.Triggers.TriggerOccurrence InitialOccurrence, WorkflowExecution Execution, long ExpectedInstanceRevision,
    AgentCore.Domain.Security.PreparedEventAdmissionSource? EventSource);

public interface ITenantWorkflowInvocationStore
{
    ValueTask<TenantAutomationAdmissionResult?> AdmitAutomationWorkflowAsync(AutomationWorkflowAdmission command, long expectedSecurityRevision, HumanSessionReference? human, Guid auditId, CancellationToken ct = default);
    ValueTask RequireWorkflowInvocationSourceAsync(WorkflowExecution execution, CancellationToken ct = default);
    ValueTask RequireWorkflowParticipantsAsync(WorkflowExecution execution, CancellationToken ct = default);
    ValueTask<WorkflowExecution> StartClaimedWorkflowAsync(ClaimedWorkflowStart command, CancellationToken ct = default);
}
