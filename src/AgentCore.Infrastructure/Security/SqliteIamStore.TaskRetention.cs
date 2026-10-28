using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Tasks;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    // Called only by the incumbent conversation-retirement transaction. Preserve
    // factual IDs, request hashes and receipts; never discard uncertain effect data.
    private static async Task RetireTaskContent(AgentCoreDbContext db, Guid tenant, DateTimeOffset at, CancellationToken ct)
    {
        // Each incumbent cleanup pass advances capped batches. Its durable deleted
        // Sessions remain eligible until all payloads have been minimized.
        var rows = await db.Set<DelegatedTaskRow>().FromSqlInterpolated($"""
            SELECT t.* FROM TenantDelegatedTasks t JOIN Sessions s ON lower(s.SessionId)=lower(coalesce(t.SourceSessionId,json_extract(t.Json,'$.Source.Workflow.OriginalSessionId'),
                CASE WHEN t.WorkflowExecutionId IS NOT NULL THEN t.WorkerSessionId END))
            WHERE t.TenantId={tenant} AND s.DurablyDeletedAtUtc IS NOT NULL AND t.State>=4
                AND json_extract(t.Json,'$.ContentRetiredAt') IS NULL
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=t.TenantId AND (r.SessionId=t.SourceSessionId OR lower(r.RootActivationId)=lower(t.RootActivationId))
                    AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
            ORDER BY t.TaskId LIMIT 100
            """).ToArrayAsync(ct);
        async Task<bool> Quiescent(Guid source, Guid? root)
        {
            var sessionId = source.ToString("D"); var rootId = root?.ToString("D");
            return !await db.AgentRuns.FromSqlInterpolated($"""
                SELECT * FROM AgentRuns WHERE TenantId={tenant} AND (SessionId={sessionId} OR RootActivationId={rootId})
                    AND (Status NOT IN (4,5,6) OR json_extract(PayloadJson,'$.sideEffect.disposition') IN (1,2,5))
                """).AnyAsync(ct);
        }
        foreach (var row in rows)
        {
            var task = TaskPersistence.Value(row);
            var source = task.Source.SessionId ?? task.Source.Workflow?.OriginalSessionId ?? task.WorkerSessionId;
            if (source is null || !await Quiescent(source.Value, task.Source.RootActivationId)) continue;
            var retired = task.RetireContent(at);
            db.Entry(row).CurrentValues.SetValues(TaskPersistence.Row(retired));
            foreach (var command in await db.Set<TaskCommandRow>().FromSqlInterpolated($"SELECT * FROM TenantTaskCommands WHERE TenantId={tenant} AND TaskId={task.TaskId} AND json_type(Json,'$.Request')='object'").ToArrayAsync(ct))
                command.Json = JsonSerializer.Serialize(TaskPersistence.Value<DelegatedTask>(command.Json).RetireContent(at));
        }
        // Approval payloads contain a second copy of the original objective and
        // canonical action. Keep only immutable technical evidence after quiescence.
        var approvals = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT a.* FROM TenantApprovalRequests a JOIN Sessions s ON s.SessionId=a.ResourceId
            WHERE a.TenantId={tenant} AND a.Permission='tasks.delegate' AND a.ContentRetiredAt IS NULL
                AND s.DurablyDeletedAtUtc IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=a.TenantId AND (r.SessionId=a.ResourceId OR r.RootActivationId=a.RootActivationId)
                    AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                AND NOT EXISTS(SELECT 1 FROM ApprovalProcessedSignals signal WHERE signal.RequestId=a.RequestId AND signal.ProcessedAt IS NULL)
            ORDER BY a.RequestId LIMIT 100
            """).ToArrayAsync(ct);
        foreach (var row in approvals)
        {
            var approval = row.Domain(); var task = approval.Operation.TaskDelegation!.Task;
            if (!await Quiescent(task.Source.RequireSessionId(), task.Source.RootActivationId)
                || await db.Set<ApprovalProcessedSignal>().AnyAsync(s => s.RequestId == row.RequestId && s.ProcessedAt == null, ct)) continue;
            if (approval.Status == TenantApprovalStatus.Pending)
            {
                approval = approval.Decide(at >= approval.ExpiresAt ? TenantApprovalStatus.Expired : TenantApprovalStatus.Cancelled, approval.Revision, at);
                db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(approval));
                foreach (var grant in await db.Set<TenantOperationGrant>().Where(g => g.RequestId == row.RequestId && g.Status == OperationGrantStatus.Available).ToArrayAsync(ct))
                    db.Entry(grant).CurrentValues.SetValues(grant with { Status = OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) });
                await db.SaveChangesAsync(ct);
            }
            row.ContentRetiredAt = at.ToUnixTimeMilliseconds(); row.Revision = checked(row.Revision + 1);
            // SQLite's exact json_object spelling is part of the retirement guard.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE TenantApprovalRequests SET ContentRetiredAt={row.ContentRetiredAt},Revision={row.Revision},
                    PayloadJson=json_object('contentRetiredAt',{row.ContentRetiredAt},
                        'taskId',json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.TaskId'),
                        'sourceSessionId',json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId'),
                        'workerInstanceId',json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceId'),
                        'workerPrincipalId',json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.PrincipalId'),'actionHash',Fingerprint)
                    WHERE TenantId={tenant} AND RequestId={row.RequestId} AND ContentRetiredAt IS NULL
                """, ct);
            db.Entry(row).State = EntityState.Detached;
        }
        var messages = await db.Set<AgentMessageRow>().FromSqlInterpolated($"""
            SELECT m.* FROM TenantAgentMessages m JOIN Sessions s ON s.SessionId=json_extract(m.Json,'$.SourceSessionId')
            JOIN TenantAgentThreads thread ON thread.TenantId=m.TenantId AND thread.ThreadId=m.ThreadId
            LEFT JOIN TenantDelegatedTasks task ON task.TenantId=m.TenantId AND task.TaskId=thread.TaskId
            LEFT JOIN AgentRuns source ON source.TenantId=m.TenantId AND source.AgentRunId=json_extract(m.Json,'$.SourceRunId')
            WHERE m.TenantId={tenant} AND s.DurablyDeletedAtUtc IS NOT NULL AND json_extract(m.Json,'$.ContentRetiredAt') IS NULL
                AND coalesce(task.RootActivationId,source.RootActivationId) IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=m.TenantId AND (r.SessionId=s.SessionId OR lower(r.RootActivationId)=lower(coalesce(task.RootActivationId,source.RootActivationId)))
                    AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=m.TenantId AND r.AgentRunId=json_extract(m.ReceiptJson,'$.WakeRunId')
                    AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
            ORDER BY m.MessageId LIMIT 100
            """).ToArrayAsync(ct);
        foreach (var row in messages)
        {
            var message = TaskPersistence.Value<AgentMessage>(row.Json);
            if (message.SourceSessionId is not { } source) continue;
            var thread = await db.Set<AgentThreadRow>().SingleAsync(t => t.TenantId == tenant && t.ThreadId == row.ThreadId, ct);
            Guid? root = thread.TaskId is { } taskId
                ? await db.Set<DelegatedTaskRow>().Where(t => t.TenantId == tenant && t.TaskId == taskId).Select(t => (Guid?)t.RootActivationId).SingleAsync(ct)
                : Guid.TryParse(await db.AgentRuns.Where(r => r.TenantId == tenant && r.AgentRunId == message.SourceRunId!.Value.ToString("D"))
                    .Select(r => r.RootActivationId).SingleOrDefaultAsync(ct), out var sourceRoot) ? sourceRoot : null;
            if (root is null || !await Quiescent(source, root)) continue;
            var receipt = TaskPersistence.Value<AgentMessageReceipt>(row.ReceiptJson);
            if (receipt.WakeSessionId is { } wakeSession && !await Quiescent(wakeSession, receipt.WakeRootActivationId)) continue;
            row.Json = JsonSerializer.Serialize(message.RetireContent(at));
            if (receipt.State == AgentMessageDelivery.NotificationPending)
                row.ReceiptJson = JsonSerializer.Serialize(receipt with { State = AgentMessageDelivery.Undeliverable, Reason = "message_source_unavailable", Revision = checked(receipt.Revision + 1), UpdatedAt = at });
        }
    }
}
