using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTaskExecutionSourcesAsync(AgentCore.Domain.Conversation.AgentRun worker, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var task = worker.Admission.DelegatedTask is { } work ? delegatedTasks.GetValueOrDefault((worker.Owner.TenantId!.Value, work.TaskId)) : null;
            if (task is null) throw new AccessDeniedException("task_source_unavailable");
            var tenant = task.Source.TenantId; var state = _tenants[tenant];
            var source = sessionBacking.ReadTenantSession(tenant, task.Source.SessionId) ?? throw new AccessDeniedException("task_source_unavailable");
            var sender = FindTenantAgentAsync(tenant, task.Source.InstanceId, ct).Result ?? throw new AccessDeniedException("task_source_unavailable");
            var target = FindTenantAgentAsync(tenant, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
            TaskReadyRules.RequireSourceAsync(task, source, task.Source.RunId is { } id ? sessionBacking.ReadTenantRun(tenant, id) : null,
                sender, InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(sender), sender.Instance.SettingsOverrides), state, new(this), TaskNow, ct, TaskConsent(task, state, ct)).AsTask().GetAwaiter().GetResult();
            TaskAuthority.TargetAsync(state, task.Source.PrincipalId, target, new(this), ct).AsTask().GetAwaiter().GetResult();
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<bool> AdmitReadyTaskAsync(ReadyTaskAdmission admission, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var tenant = admission.TenantId; var task = delegatedTasks.GetValueOrDefault((tenant, admission.TaskId));
            if (task is null || task.State != DelegatedTaskState.PendingDependencies || task.Revision != admission.ExpectedRevision) return ValueTask.FromResult(false);
            var state = _tenants[tenant]; var source = sessionBacking.ReadTenantSession(tenant, task.Source.SessionId) ?? throw new AccessDeniedException("task_source_unavailable");
            var agent = FindTenantAgentAsync(tenant, task.Source.InstanceId, ct).Result ?? throw new AccessDeniedException("task_source_unavailable");
            var sourceRun = task.Source.RunId is { } sourceId ? sessionBacking.ReadTenantRun(tenant, sourceId) : null;
            TaskReadyRules.RequireSourceAsync(task, source, sourceRun, agent,
                InstanceSettingsResolver.Resolve(TenantAgentDefinitionBaseline(agent), agent.Instance.SettingsOverrides), state, new(this), TaskNow, ct, TaskConsent(task, state, ct)).AsTask().GetAwaiter().GetResult();
            var target = FindTenantAgentAsync(tenant, task.Assignment.InstanceId, ct).Result ?? throw new AccessDeniedException("task_target_unavailable");
            TaskAuthority.TargetAsync(state, task.Source.PrincipalId, target, new(this), ct).AsTask().GetAwaiter().GetResult();
            var reservation = _approvals.Values.SingleOrDefault(r => r.TenantId == tenant && r.Operation.TaskDelegation?.Task.TaskId == task.TaskId);
            if (reservation is not null && TenantTaskConsentRules.IsHuman(reservation) && admission.Run.AgentRunId != reservation.RunId)
                throw new AccessDeniedException("task_admission_mismatch");
            var next = TaskReadyRules.Graph(admission, task, task.Request.DependsOnTaskIds.Select(id => delegatedTasks[(tenant, id)]).ToArray(), target, TaskNow);
            RequireSpaceExecutionSource(admission.Run, state, new(this), ct, admission: true).AsTask().GetAwaiter().GetResult();
            AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(admission.Run.PinnedModel, modelSettings.GetValueOrDefault(tenant)?.Revision ?? 0);
            StageTaskGraph(new(tenant, admission.Run.SessionId, target.Instance.InstanceId, target.PrincipalId, task.Source.OriginalInitiatorPrincipalId ?? target.PrincipalId, admission.Run.CreatedAtUtc), admission.Session, admission.Run);
            delegatedTasks[(tenant, task.TaskId)] = next; BumpTaskSecurity(state);
            _audit.Add(new(admission.AuditId, tenant, target.PrincipalId, "tasks.ready", task.TaskId.ToString("D"), "allowed", "dependencies_succeeded", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Ready, next);
            AgentCore.Application.Observability.TaskTelemetry.DeliveryLag("dependency", task.CreatedAt, admission.Run.CreatedAtUtc);
            return ValueTask.FromResult(true);
        }
    }
}
