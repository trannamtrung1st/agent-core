using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TaskCommandReceipt?> ReadTaskCommandReceiptAsync(Guid tenant, TaskActor actor, Guid task, string kind, string requestKey, Guid? notification, CancellationToken ct = default)
    {
        _ = await ReadTaskNotificationsAsync(tenant, actor, task, ct);
        if (kind is not ("cancel" or "acknowledge") || kind == "acknowledge" && notification is null) throw new ArgumentException("Select the exact command kind and notice.");
        var key = (kind == "acknowledge" ? "ack:" : "") + TaskLimits.Key(requestKey);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<TaskCommandRow>().AsNoTracking().SingleOrDefaultAsync(c => c.TenantId == tenant && c.TaskId == task && c.ActorId == actor.PrincipalId && c.RequestKey == key, ct);
        if (row is null) return null;
        if (kind == "cancel") return new(Task: TaskPersistence.Value<DelegatedTask>(row.Json));
        var notice = TaskPersistence.Value<TaskNotification>(row.Json); return notice.NotificationId == notification ? new(Notification: notice) : null;
    }
    public async ValueTask<AgentMessageResult?> ReadMessageReceiptAsync(Guid tenant, TaskActor actor, Guid taskId, string requestKey, CancellationToken ct = default)
    {
        var authorized = await ReadMessagesPageAsync(tenant, actor, taskId, null, null, 1, ct);
        var scope = actor.Agent is { } frame ? $"run:{frame.RunId:D}" : $"human:{actor.PrincipalId:D}:task:{taskId:D}";
        var key = TaskLimits.Key(requestKey); await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Set<AgentMessageRow>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenant && m.SenderPrincipalId == actor.PrincipalId && m.SourceScope == scope && m.RequestKey == key, ct);
        if (row is null) return null;
        var thread = await db.Set<AgentThreadRow>().AsNoTracking().SingleAsync(t => t.TenantId == tenant && t.ThreadId == row.ThreadId && t.TaskId == taskId, ct);
        return new(TaskPersistence.Value<AgentThread>(thread.Json), TaskPersistence.Value<AgentMessage>(row.Json), TaskPersistence.Value<AgentMessageReceipt>(row.ReceiptJson), authorized.SecurityRevision, true);
    }
    public async ValueTask<TaskPage<TaskNotification>> ReadTaskNotificationsAsync(Guid tenant, TaskActor actor, Guid taskId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await RequireTaskReader(db, tenant, actor, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == taskId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var task = TaskPersistence.Value(row);
        if (await ReadSandboxSession(db, tenant, task.Source.SessionId, ct) is not { } source || AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow) || !await CanReadTaskAsync(db, state, actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
        var items = await db.Set<TaskNotificationRow>().AsNoTracking().Where(n => n.TenantId == tenant && n.TaskId == taskId).ToArrayAsync(ct);
        return new(items.Select(n => TaskPersistence.Value<TaskNotification>(n.Json)).ToArray(), null, false, state.Tenant.Revision);
    }
    public async ValueTask<TaskNotification> AcknowledgeTaskAsync(TaskAcknowledge command, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = command.TenantId; await RequireTaskReader(db, tenant, command.Actor, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var taskRow = await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == command.TaskId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var task = TaskPersistence.Value(taskRow);
        if (await ReadSandboxSession(db, tenant, task.Source.SessionId, ct) is not { } source || AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow) || !await CanReadTaskAsync(db, state, command.Actor, task, ct)
            || command.Actor.Agent is { } frame && frame.SessionId != task.Source.SessionId) throw new AccessDeniedException("resource_unavailable");
        if (command.Actor.Human is not null) await TaskAuthority.RequireAsync(state, command.Actor.PrincipalId, "sessions.manage",
            TenantSessionRules.Scope(tenant, task.Source.SessionId), new(this), ct);
        var row = await db.Set<TaskNotificationRow>().SingleOrDefaultAsync(n => n.TenantId == tenant && n.TaskId == task.TaskId && n.NotificationId == command.NotificationId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var notification = TaskPersistence.Value<TaskNotification>(row.Json);
        var key = "ack:" + TaskLimits.Key(command.RequestKey); var hash = TaskLimits.Hash(new { command.TaskId, command.NotificationId, command.ExpectedRevision });
        var previous = await db.Set<TaskCommandRow>().SingleOrDefaultAsync(c => c.TenantId == tenant && c.TaskId == task.TaskId && c.ActorId == command.Actor.PrincipalId && c.RequestKey == key, ct);
        if (previous is not null)
        {
            if (previous.Hash != hash) throw new SecurityConflictException("task_request_conflict"); return TaskPersistence.Value<TaskNotification>(previous.Json);
        }
        if (state.Tenant.Revision != command.ExpectedSecurityRevision || notification.Revision != command.ExpectedRevision || notification.AcknowledgedAt is not null) throw new SecurityConflictException("stale_revision");
        var next = notification with { Revision = notification.Revision + 1, AcknowledgedAt = TaskNow, AcknowledgedBy = command.Actor.PrincipalId };
        row.Revision = next.Revision; row.Json = System.Text.Json.JsonSerializer.Serialize(next);
        db.Add(new TaskCommandRow { TenantId = tenant, TaskId = task.TaskId, ActorId = command.Actor.PrincipalId, RequestKey = key, Hash = hash, Json = row.Json });
        await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, tenant, command.Actor.PrincipalId, "tasks.acknowledge", notification.NotificationId.ToString("D"), "allowed", "notice_handled", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct);
        AgentCore.Application.Observability.TaskTelemetry.Task(AgentCore.Application.Observability.TaskCoordinationEvent.Acknowledged, task); return next;
    }
    public async ValueTask<TaskPage<AgentMessage>> ReadMessagesPageAsync(Guid tenant, TaskActor actor, Guid? taskId, Guid? threadId, Guid? cursor, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await RequireMessageReader(db, tenant, actor, ct);
        if (taskId is { } task)
        {
            var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == task, ct);
            if (threadId is not null || row is null || await ReadSandboxSession(db, tenant, TaskPersistence.Value(row).Source.SessionId, ct) is null
                || !await CanReadTaskAsync(db, state, actor, TaskPersistence.Value(row), ct)) throw new AccessDeniedException("resource_unavailable");
            threadId = await db.Set<AgentThreadRow>().Where(t => t.TenantId == tenant && t.TaskId == task).Select(t => (Guid?)t.ThreadId).SingleOrDefaultAsync(ct);
            if (threadId is null) return new([], null, false, state.Tenant.Revision);
        }
        if (threadId is null) throw new ArgumentException("Select an exact task or thread.");
        long sequence = 0;
        if (cursor is { } id)
        {
            var row = await db.Set<AgentMessageRow>().AsNoTracking().SingleOrDefaultAsync(m => m.TenantId == tenant && m.MessageId == id && m.ThreadId == threadId, ct)
                ?? throw new AccessDeniedException("resource_unavailable"); sequence = row.Sequence;
        }
        return await ReadAgentMessagesAsync(tenant, actor, threadId.Value, sequence, limit, ct);
    }
}
