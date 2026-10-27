using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Tasks;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    public ValueTask<TaskCommandReceipt?> ReadTaskCommandReceiptAsync(Guid tenant, TaskActor actor, Guid task, string kind, string requestKey, Guid? notification, CancellationToken ct = default)
    {
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            _ = ReadTaskNotificationsAsync(tenant, actor, task, ct).Result; var key = TaskLimits.Key(requestKey);
            if (kind == "cancel") return ValueTask.FromResult<TaskCommandReceipt?>(taskCommands.TryGetValue((tenant, task, actor.PrincipalId, key), out var saved) ? new(Task: saved.Task) : null);
            if (kind != "acknowledge" || notification is null) throw new ArgumentException("Select the exact command kind and notice.");
            return ValueTask.FromResult<TaskCommandReceipt?>(taskAcks.TryGetValue((tenant, notification.Value, actor.PrincipalId, key), out var ack) && ack.Receipt.TaskId == task ? new(Notification: ack.Receipt) : null);
        }
    }
    public ValueTask<AgentMessageResult?> ReadMessageReceiptAsync(Guid tenant, TaskActor actor, Guid taskId, string requestKey, CancellationToken ct = default)
    {
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            _ = ReadMessagesPageAsync(tenant, actor, taskId, null, null, 1, ct).Result;
            var scope = actor.Agent is { } frame ? $"run:{frame.RunId:D}" : $"human:{actor.PrincipalId:D}:task:{taskId:D}";
            var message = agentMessages.Values.SingleOrDefault(m => m.Message.TenantId == tenant && m.Message.SenderPrincipalId == actor.PrincipalId && m.Scope == scope && m.Message.RequestKey == TaskLimits.Key(requestKey));
            return ValueTask.FromResult<AgentMessageResult?>(message.Message is null ? null : new(agentThreads[(tenant, message.Message.ThreadId)], message.Message, message.Receipt, _tenants[tenant].Tenant.Revision, true));
        }
    }
    private readonly Dictionary<(Guid Tenant, Guid Notification, Guid Actor, string Key), (string Hash, TaskNotification Receipt)> taskAcks = [];
    public ValueTask<TaskPage<TaskNotification>> ReadTaskNotificationsAsync(Guid tenant, TaskActor actor, Guid taskId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireTaskReader(tenant, actor, ct); var state = _tenants[tenant];
            var task = delegatedTasks.GetValueOrDefault((tenant, taskId)) ?? throw new AccessDeniedException("resource_unavailable");
            if (sessionBacking.ReadTenantSession(tenant, task.Source.SessionId) is not { } source || AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow) || !CanReadTask(state, actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
            return ValueTask.FromResult(new TaskPage<TaskNotification>(taskNotifications.TryGetValue((tenant, taskId), out var notification) ? [notification] : [], null, false, state.Tenant.Revision));
        }
    }
    public ValueTask<TaskNotification> AcknowledgeTaskAsync(TaskAcknowledge command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var page = ReadTaskNotificationsAsync(command.TenantId, command.Actor, command.TaskId, ct).Result;
            var notification = page.Items.SingleOrDefault(n => n.NotificationId == command.NotificationId) ?? throw new AccessDeniedException("resource_unavailable");
            var task = delegatedTasks[(command.TenantId, command.TaskId)];
            if (command.Actor.Agent is { } frame && frame.SessionId != task.Source.SessionId) throw new AccessDeniedException("resource_unavailable");
            if (command.Actor.Human is not null) TaskAuthority.RequireAsync(_tenants[command.TenantId], command.Actor.PrincipalId, "sessions.manage",
                TenantSessionRules.Scope(command.TenantId, task.Source.SessionId), new(this), ct).AsTask().GetAwaiter().GetResult();
            var key = (command.TenantId, command.NotificationId, command.Actor.PrincipalId, TaskLimits.Key(command.RequestKey));
            var hash = TaskLimits.Hash(new { command.TaskId, command.NotificationId, command.ExpectedRevision });
            if (taskAcks.TryGetValue(key, out var replay))
            {
                if (replay.Hash != hash) throw new SecurityConflictException("task_request_conflict"); return ValueTask.FromResult(replay.Receipt);
            }
            if (page.SecurityRevision != command.ExpectedSecurityRevision || notification.Revision != command.ExpectedRevision || notification.AcknowledgedAt is not null)
                throw new SecurityConflictException("stale_revision");
            var next = notification with { Revision = notification.Revision + 1, AcknowledgedAt = TaskNow, AcknowledgedBy = command.Actor.PrincipalId };
            taskNotifications[(command.TenantId, command.TaskId)] = next; taskAcks.Add(key, (hash, next)); BumpTaskSecurity(_tenants[command.TenantId]);
            _audit.Add(new(command.AuditId, command.TenantId, command.Actor.PrincipalId, "tasks.acknowledge", notification.NotificationId.ToString("D"), "allowed", "notice_handled", TaskNow));
            AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Acknowledged, task);
            return ValueTask.FromResult(next);
        }
    }
    public ValueTask<TaskPage<AgentMessage>> ReadMessagesPageAsync(Guid tenant, TaskActor actor, Guid? taskId, Guid? threadId, Guid? cursor, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            RequireMessageReader(tenant, actor, ct);
            if (taskId is { } task)
            {
                if (threadId is not null || delegatedTasks.GetValueOrDefault((tenant, task)) is not { } saved
                    || sessionBacking.ReadTenantSession(tenant, saved.Source.SessionId) is null
                    || !CanReadTask(_tenants[tenant], actor, saved, ct)) throw new AccessDeniedException("resource_unavailable");
                threadId = agentThreads.Values.SingleOrDefault(t => t.TenantId == tenant && t.TaskId == task)?.ThreadId;
                if (threadId is null) return ValueTask.FromResult(new TaskPage<AgentMessage>([], null, false, _tenants[tenant].Tenant.Revision));
            }
            if (threadId is null) throw new ArgumentException("Select an exact task or thread.");
            long sequence = 0;
            if (cursor is { } id)
            {
                if (!agentMessages.TryGetValue((tenant, id), out var row) || row.Message.ThreadId != threadId) throw new AccessDeniedException("resource_unavailable");
                sequence = row.Message.Sequence;
            }
            return ReadAgentMessagesAsync(tenant, actor, threadId.Value, sequence, limit, ct);
        }
    }
}
