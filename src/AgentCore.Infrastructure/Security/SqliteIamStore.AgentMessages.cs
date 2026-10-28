using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Domain.Tasks;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantAgentMessageStore
{
    public async ValueTask<AgentMessageResult> AppendAgentMessageAsync(AgentMessageCommand command, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        command.Actor.Validate(); var tenant = command.TenantId;
        var taskRow = command.TaskId is { } taskId ? await db.Set<DelegatedTaskRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == taskId, ct)
            ?? throw new AccessDeniedException("resource_unavailable") : null;
        var task = taskRow is null ? null : TaskPersistence.Value(taskRow);
        if (task?.Source.Workflow is not null) throw new AccessDeniedException("workflow_task_messages_unavailable");
        var sourceSession = command.Actor.Agent?.SessionId ?? task?.Source.SessionId ?? throw new AccessDeniedException("agent_sender_required");
        var (source, run, sender, state) = await RequireTaskSource(db, tenant, sourceSession, command.Actor, "agent-messages.send", ct, permissionScope: task is not null ? TaskAuthority.Scope(tenant, task.TaskId) : command.ThreadId is { } selectedThread
                ? AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent-thread", selectedThread.ToString("D"))
                : AgentCore.Domain.Security.AccessScope.ForResource(tenant, "agent", command.Actor.Agent!.Owner.AgentInstanceId.ToString("D")));
        if (task is not null && !await CanReadTaskAsync(db, state, command.Actor, task, ct)) throw new AccessDeniedException("resource_unavailable");
        AgentThreadRow? row; AgentThread thread;
        if (task is not null)
        {
            if (task.WorkerSessionId is null || command.RecipientInstanceId is not null) throw new AccessDeniedException("task_message_unavailable");
            row = await db.Set<AgentThreadRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == task.TaskId, ct);
            thread = row is null ? new(tenant, command.NewThreadId, task.Source.InstanceId, task.Assignment.InstanceId, task.TaskId, task.Source.SessionId, task.WorkerSessionId, TaskNow)
                : TaskPersistence.Value<AgentThread>(row.Json);
        }
        else
        {
            if (command.Actor.Human is not null || command.Actor.Tool != "agent-messages.send" || command.RecipientInstanceId is not { } recipientId) throw new AccessDeniedException("agent_sender_required");
            var recipientRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == recipientId.ToString("D"), ct) ?? throw new AccessDeniedException("agent_message_unavailable");
            await AgentMessageRules.DirectPolicyAsync(state, sender, Owned(recipientRow), new(this), ct);
            var shape = new AgentThread(tenant, command.NewThreadId, sender.Instance.InstanceId, recipientId, null, null, null, TaskNow);
            row = await db.Set<AgentThreadRow>().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == null && t.RetiredAt == null && t.FirstInstanceId == shape.FirstInstanceId && t.SecondInstanceId == shape.SecondInstanceId, ct);
            if (row is not null && !await HasVisibleThreadContent(db, tenant, row.ThreadId, ct))
            {
                // A fresh correspondence starts a new generation. Old IDs and sequence
                // values never become a handle for newly retained private content.
                row.RetiredAt = TaskNow.ToUnixTimeMilliseconds();
                await db.SaveChangesAsync(ct);
                row = null;
            }
            thread = row is null ? shape : TaskPersistence.Value<AgentThread>(row.Json);
        }
        if (command.ThreadId is { } selected && selected != thread.ThreadId) throw new AccessDeniedException("resource_unavailable");
        var scope = AgentMessageRules.SourceScope(command); var hash = AgentMessageRules.Fingerprint(command); var key = TaskLimits.Key(command.RequestKey);
        var previous = await db.Set<AgentMessageRow>().SingleOrDefaultAsync(m => m.TenantId == tenant && m.SenderPrincipalId == command.Actor.PrincipalId && m.SourceScope == scope && m.RequestKey == key, ct);
        if (previous is not null)
        {
            if (previous.RequestHash != hash || previous.ThreadId != thread.ThreadId) throw new SecurityConflictException("message_request_conflict");
            var previousMessage = TaskPersistence.Value<AgentMessage>(previous.Json); var previousReceipt = TaskPersistence.Value<AgentMessageReceipt>(previous.ReceiptJson);
            AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.Replayed, previousMessage, previousReceipt);
            return new(thread, previousMessage, previousReceipt, state.Tenant.Revision, true);
        }
        if (task is not null && TaskLimits.Terminal(task.State)) throw new AccessDeniedException("task_message_unavailable");
            if (state.Tenant.Revision != command.ExpectedSecurityRevision) throw new SecurityConflictException("stale_revision");
        var message = AgentMessageRules.Create(command, thread, source, run, (row?.Sequence ?? 0) + 1, TaskNow);
        var receiverRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == message.RecipientInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("message_recipient_unavailable");
        var receiver = Owned(receiverRow);
        await TaskAuthority.RequireAsync(state, receiver.PrincipalId, "agent-messages.read", task is null
            ? AccessScope.ForResource(tenant, "agent", receiver.Instance.InstanceId.ToString("D")) : TaskAuthority.Scope(tenant, task.TaskId), new(this), ct);
        var wakeEnabled = task is null && (await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == message.RecipientInstanceId.ToString("D"), ct)) is { } wakeRecipient && Owned(wakeRecipient).Instance.Cooperation?.AllowDirectMessageWake == true;
        var receipt = new AgentMessageReceipt(tenant, message.MessageId, message.RecipientInstanceId,
            wakeEnabled ? AgentMessageDelivery.NotificationPending : AgentMessageDelivery.DeliveredToAuthorizedInbox, 1, TaskNow); receipt.Validate();
        if (row is null)
        {
            row = new AgentThreadRow { TenantId = tenant, ThreadId = thread.ThreadId, FirstInstanceId = thread.FirstInstanceId, SecondInstanceId = thread.SecondInstanceId,
                TaskId = thread.TaskId, Json = System.Text.Json.JsonSerializer.Serialize(thread) }; db.Add(row);
        }
        row.Sequence = message.Sequence;
        db.Add(new AgentMessageRow { TenantId = tenant, ThreadId = thread.ThreadId, MessageId = message.MessageId, SenderPrincipalId = message.SenderPrincipalId,
            SourceScope = scope, RecipientInstanceId = message.RecipientInstanceId, RequestKey = key, RequestHash = hash, Sequence = message.Sequence,
            Json = System.Text.Json.JsonSerializer.Serialize(message), ReceiptJson = System.Text.Json.JsonSerializer.Serialize(receipt) });
        await BumpTaskSecurity(db, state, ct);
        db.Add(new AuthorizationAudit(command.AuditId, tenant, command.Actor.PrincipalId, "agent-messages.send", message.MessageId.ToString("D"), "allowed", "message_recorded", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); AgentCore.Application.Observability.TaskTelemetry.Message(AgentCore.Application.Observability.AgentMessageEvent.Appended, message, receipt);
        return new(thread, message, receipt, state.Tenant.Revision + 1);
    }
    private async Task RequireMessageReader(AgentCoreDbContext db, Guid tenant, TaskActor actor, CancellationToken ct)
    {
        actor.Validate(); if (actor.Human is { } human) await RequireHumanSessionAsync(db, tenant, human, ct);
        else await RequireTaskSource(db, tenant, actor.Agent!.SessionId, actor, "sessions.read", ct, false);
    }
    private async Task<bool> CanReadAgentThread(AgentCoreDbContext db, TenantSecurityState state, TaskActor actor, AgentThread thread, CancellationToken ct)
    {
        if (!await AgentMessageRules.CanReadThreadAsync(state, actor, thread, new(this), ct, actor.Agent is { } frame
            ? await CompletionRun(db, state.Tenant.TenantId, frame.RunId, ct) : null)) return false;
        if (thread.TaskId is not { } taskId) return true;
        var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == thread.TenantId && t.TaskId == taskId, ct);
        if (row is null) return false; var task = TaskPersistence.Value(row);
        var source = await ReadSandboxSession(db, thread.TenantId, task.Source.RequireSessionId(), ct);
        return source is not null && !AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow)
            && await CanReadTaskAsync(db, state, actor, task, ct);
    }
    private async Task<bool> HasVisibleThreadContent(AgentCoreDbContext db, Guid tenant, Guid thread, CancellationToken ct)
    {
        long? before = null;
        while (true)
        {
            var batch = await db.Set<AgentMessageRow>().FromSqlInterpolated($"""
                SELECT m.* FROM TenantAgentMessages m WHERE m.TenantId={tenant} AND m.ThreadId={thread}
                    AND ({before} IS NULL OR m.Sequence<{before}) ORDER BY m.Sequence DESC LIMIT 100
                """).AsNoTracking().ToArrayAsync(ct);
            if (batch.Length == 0) return false;
            var sources = batch.Select(m => TaskPersistence.Value<AgentMessage>(m.Json).SourceSessionId).OfType<Guid>().Select(id => id.ToString("D")).Distinct().ToArray();
            if (await OwnedSessions(db, tenant).AnyAsync(s => sources.Contains(s.SessionId), ct)) return true;
            if (batch.Length < 100) return false;
            before = batch[^1].Sequence;
        }
    }
    public async ValueTask<TaskPage<AgentThread>> ReadAgentThreadsAsync(TaskQuery query, CancellationToken ct = default)
    {
        if (query.Limit is < 1 or > 100) throw new ArgumentException("Invalid thread page.");
        await using var db = await contexts.CreateDbContextAsync(ct); await RequireMessageReader(db, query.TenantId, query.Actor, ct);
        var state = await ReadTenantAsync(db, query.TenantId, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var candidates = db.Set<AgentThreadRow>().AsNoTracking().Where(t => t.TenantId == query.TenantId && t.RetiredAt == null);
        if (query.Scope == "direct") candidates = candidates.Where(t => t.TaskId == null);
        if (query.InstanceId is { } instance) candidates = candidates.Where(t => t.FirstInstanceId == instance || t.SecondInstanceId == instance);
        var result = new List<AgentThread>(); Guid? after = query.Cursor;
        while (result.Count <= query.Limit)
        {
            var batch = await (after is { } cursor ? candidates.Where(t => t.ThreadId.CompareTo(cursor) > 0) : candidates).OrderBy(t => t.ThreadId).Take(100).ToArrayAsync(ct);
            if (batch.Length == 0) break;
            foreach (var row in batch)
            {
                after = row.ThreadId; var thread = TaskPersistence.Value<AgentThread>(row.Json);
                if (!await CanReadAgentThread(db, state, query.Actor, thread, ct)) continue;
                if (!await HasVisibleThreadContent(db, query.TenantId, thread.ThreadId, ct)) continue;
                result.Add(thread); if (result.Count > query.Limit) break;
            }
            if (batch.Length < 100) break;
        }
        return new(result.Take(query.Limit).ToArray(), result.Count > query.Limit ? result[query.Limit - 1].ThreadId : null, result.Count > query.Limit, state.Tenant.Revision);
    }
    public async ValueTask<TaskPage<AgentMessage>> ReadAgentMessagesAsync(Guid tenant, TaskActor actor, Guid threadId, long afterSequence, int limit, CancellationToken ct = default)
    {
        if (afterSequence < 0 || limit is < 1 or > TaskLimits.MaxMessagePage) throw new ArgumentException("Invalid message page.");
        await using var db = await contexts.CreateDbContextAsync(ct); await RequireMessageReader(db, tenant, actor, ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        var row = await db.Set<AgentThreadRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.ThreadId == threadId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var thread = TaskPersistence.Value<AgentThread>(row.Json);
        if (row.RetiredAt is not null || !await CanReadAgentThread(db, state, actor, thread, ct)) throw new AccessDeniedException("resource_unavailable");
        if (thread.TaskId is null && !await HasVisibleThreadContent(db, tenant, threadId, ct)) throw new AccessDeniedException("resource_unavailable");
        var items = new List<AgentMessage>(); var after = afterSequence;
        while (items.Count <= limit)
        {
            var batch = await db.Set<AgentMessageRow>().AsNoTracking().Where(m => m.TenantId == tenant && m.ThreadId == threadId && m.Sequence > after).OrderBy(m => m.Sequence).Take(100).ToArrayAsync(ct);
            foreach (var item in batch)
            {
                after = item.Sequence; var message = TaskPersistence.Value<AgentMessage>(item.Json);
                if (message.SourceSessionId is not { } sourceId || await ReadSandboxSession(db, tenant, sourceId, ct) is not { } source
                    || AgentCore.Domain.Conversation.SessionRetention.IsDue(source.Snapshot, TaskNow)) continue;
                items.Add(message); if (items.Count > limit) break;
            }
            if (batch.Length < 100) break;
        }
        return new(items.Take(limit).ToArray(), items.Count > limit ? items[limit - 1].MessageId : null, items.Count > limit, state.Tenant.Revision);
    }
}
