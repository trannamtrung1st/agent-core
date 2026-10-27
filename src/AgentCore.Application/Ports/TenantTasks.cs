using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;

namespace AgentCore.Application.Ports;

public sealed record TaskActor(HumanSessionReference? Human = null, TenantRuntimeFrame? Agent = null, string? Tool = null)
{
    public Guid PrincipalId => Human?.PrincipalId ?? Agent?.Owner.ExecutorPrincipalId ?? throw new ArgumentException("A real task actor is required.");
    public void Validate()
    {
        if ((Human is null) == (Agent is null) || Human is not null && Tool is not null || Agent is not null && Tool is null)
            throw new ArgumentException("Exactly one authenticated human or claimed Agent actor is required.");
    }
}
public sealed record TaskAdmission(Guid TenantId, Guid SourceSessionId, TaskActor Actor, TaskRequest Request,
    DelegatedTask Task, SessionSnapshot? WorkerSession, AgentRun? WorkerRun, long ExpectedSecurityRevision, Guid AuditId);
public sealed record TaskMutationResult(DelegatedTask Task, long SecurityRevision, bool Replayed = false);
public sealed record TaskPreparation(TaskAdmission? Admission, TaskMutationResult? Replay);
public enum TaskPreparationPurpose { Direct, Consent }
public sealed record TaskCommandReceipt(DelegatedTask? Task = null, TaskNotification? Notification = null);
public sealed record TaskPage<T>(IReadOnlyList<T> Items, Guid? NextCursor, bool HasMore, long SecurityRevision, bool CanDelegate = false);
public sealed record TaskQuery(Guid TenantId, TaskActor Actor, Guid? SessionId = null, Guid? InstanceId = null,
    string Scope = "created", Guid? Cursor = null, int Limit = 50);
public sealed record TaskCancel(Guid TenantId, Guid TaskId, TaskActor Actor, long ExpectedRevision,
    long ExpectedSecurityRevision, string RequestKey, string? Reason, Guid AuditId);
public sealed record TaskAcknowledge(Guid TenantId, Guid TaskId, Guid NotificationId, TaskActor Actor, long ExpectedRevision,
    long ExpectedSecurityRevision, string RequestKey, Guid AuditId);
public sealed record AgentMessageCommand(Guid TenantId, TaskActor Actor, Guid? TaskId, Guid? ThreadId,
    Guid? RecipientInstanceId, AgentMessageKind Kind, string Text, string RequestKey, long ExpectedSecurityRevision,
    Guid MessageId, Guid NewThreadId, Guid AuditId);
public sealed record AgentMessageResult(AgentThread Thread, AgentMessage Message, AgentMessageReceipt Receipt, long SecurityRevision, bool Replayed = false);
public sealed record TaskReconciliation(DelegatedTask Task, TenantSession? Source, AgentRun? Worker, IReadOnlyList<DelegatedTask> Dependencies);
public sealed record ReadyTaskAdmission(Guid TenantId, Guid TaskId, long ExpectedRevision, SessionSnapshot Session, AgentRun Run, Guid AuditId);
public sealed record AgentMessageWakeSource(AgentThread Thread, AgentMessage Message, AgentMessageReceipt Receipt, AgentRun SourceRun, AgentMessageWork? WakeAncestor = null);
public sealed record AgentMessageWakeAdmission(AgentMessageWakeSource Source, SessionSnapshot Session, AgentRun Run, Guid AuditId);
public sealed record TaskExecutionAllowance(int MaxSteps, int DurationSeconds);

/// <summary>Facets of the incumbent IAM transaction. These are internal server ports, never public authorization shortcuts.</summary>
public interface ITenantTaskStore
{
    ValueTask<AgentCore.Domain.Tasks.TaskBudgetReservation> ProposeTaskBudgetAsync(Guid tenant, Guid sourceSession, TaskActor actor,
        AgentCore.Domain.Definitions.ExecutionBudgetProfile workerBudget, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct, bool reportOnCompletion = false);
    ValueTask<TaskExecutionAllowance?> ReadTaskExecutionAllowanceAsync(AgentRun run, CancellationToken ct = default);
    ValueTask<IReadOnlyDictionary<Guid, int>> ReadTaskCapacityAsync(Guid tenant, IReadOnlyList<Guid> candidates, CancellationToken ct = default);
    ValueTask<DelegatedTask?> FindTaskAsync(Guid tenant, Guid task, CancellationToken ct = default);
    ValueTask<TaskMutationResult?> FindTaskRequestAsync(Guid tenant, Guid sourceSession, TaskActor actor, string requestKey, CancellationToken ct = default, TaskPreparationPurpose purpose = TaskPreparationPurpose.Direct);
    ValueTask<TaskMutationResult> AdmitTaskAsync(TaskAdmission admission, CancellationToken ct = default);
    ValueTask<TaskPage<DelegatedTask>> ReadTasksAsync(TaskQuery query, CancellationToken ct = default);
    ValueTask<TaskMutationResult> CancelTaskAsync(TaskCancel command, CancellationToken ct = default);
    ValueTask<TaskPage<TaskNotification>> ReadTaskNotificationsAsync(Guid tenant, TaskActor actor, Guid task, CancellationToken ct = default);
    ValueTask<TaskNotification> AcknowledgeTaskAsync(TaskAcknowledge command, CancellationToken ct = default);
    ValueTask<TaskCommandReceipt?> ReadTaskCommandReceiptAsync(Guid tenant, TaskActor actor, Guid task, string kind, string requestKey, Guid? notification, CancellationToken ct = default);
    ValueTask<IReadOnlyList<TaskReconciliation>> ListTaskReconciliationAsync(int limit, CancellationToken ct = default);
    ValueTask<bool> ReconcileTaskAsync(Guid tenant, Guid task, long expectedRevision, Guid auditId, CancellationToken ct = default, string? readinessRefusal = null);
    ValueTask<bool> AdmitReadyTaskAsync(ReadyTaskAdmission admission, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentCore.Application.Tasks.TaskReportSource>> ListTaskReportsAsync(int limit, CancellationToken ct = default);
    ValueTask<bool> AdmitTaskReportAsync(AgentCore.Application.Tasks.TaskReportAdmission admission, CancellationToken ct = default);
    ValueTask WithholdTaskReportAsync(Guid tenant, Guid task, long noticeRevision, string reason, CancellationToken ct = default);
    ValueTask RequireTaskReportSourcesAsync(AgentRun report, CancellationToken ct = default);
    ValueTask RequireTaskExecutionSourcesAsync(AgentRun worker, CancellationToken ct = default);
    ValueTask RequireTaskOutcomeSourcesAsync(AgentRun worker, CancellationToken ct = default);
}
public interface ITenantAgentMessageStore
{
    ValueTask RequireAgentMessageExecutionSourcesAsync(AgentRun wake, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentMessageWakeSource>> ListAgentMessageWakesAsync(int limit, CancellationToken ct = default);
    ValueTask<bool> AdmitAgentMessageWakeAsync(AgentMessageWakeAdmission admission, CancellationToken ct = default);
    ValueTask WithholdAgentMessageWakeAsync(Guid tenant, Guid message, long revision, string reason, CancellationToken ct = default);
    ValueTask<AgentMessageResult?> FindAgentMessageSourceAsync(Guid tenant, Guid message, CancellationToken ct = default);
    ValueTask<AgentMessageResult> AppendAgentMessageAsync(AgentMessageCommand command, CancellationToken ct = default);
    ValueTask<TaskPage<AgentThread>> ReadAgentThreadsAsync(TaskQuery query, CancellationToken ct = default);
    ValueTask<TaskPage<AgentMessage>> ReadAgentMessagesAsync(Guid tenant, TaskActor actor, Guid thread, long afterSequence, int limit, CancellationToken ct = default);
    ValueTask<TaskPage<AgentMessage>> ReadMessagesPageAsync(Guid tenant, TaskActor actor, Guid? task, Guid? thread, Guid? cursor, int limit, CancellationToken ct = default);
    ValueTask<AgentMessageResult?> ReadMessageReceiptAsync(Guid tenant, TaskActor actor, Guid task, string requestKey, CancellationToken ct = default);
}
