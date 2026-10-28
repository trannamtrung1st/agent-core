using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tasks;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<IReadOnlyList<TaskWaitFact>> TenantTaskWaitFacts(AgentCoreDbContext db, AgentRun run, AgentRunWait wait, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value; var state = await ReadTenantAsync(db, tenant, ct); var result = new List<TaskWaitFact>();
        var actor = new TaskActor(Agent: new(run.Owner, run.SessionId, run.AgentRunId, run.Claim?.Generation ?? wait.SuspendedGeneration ?? Guid.Empty), Tool: "execution.wait");
        foreach (var witness in wait.Tasks ?? [])
        {
            var row = await db.Set<DelegatedTaskRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == witness.TaskId, ct);
            var task = row is null ? null : TaskPersistence.Value(row);
            var source = task is null ? null : await ReadSandboxSession(db, tenant, task.Source.RequireSessionId(), ct);
            var available = state is not null && task is not null && source is not null && !SessionRetention.IsDue(source.Snapshot, TaskNow)
                && (task.Source.SessionId == run.SessionId || task.WorkerSessionId == run.SessionId)
                && await CanReadTaskAsync(db, state, actor, task, ct);
            long sequence = 0;
            if (available && await TaskWaitRules.CanReadMessagesAsync(state!, run.Owner.ExecutorPrincipalId!.Value, task!, new(this), ct, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId))
            {
                var thread = await db.Set<AgentThreadRow>().AsNoTracking().SingleOrDefaultAsync(t => t.TenantId == tenant && t.TaskId == task!.TaskId, ct);
                if (thread is not null)
                {
                    var newest = await db.Set<AgentMessageRow>().FromSqlInterpolated($"""
                        SELECT * FROM TenantAgentMessages WHERE TenantId={tenant} AND ThreadId={thread.ThreadId}
                            AND json_extract(Json,'$.SourceSessionId')<>{run.SessionId.ToString("D")} ORDER BY Sequence DESC LIMIT 1
                        """).AsNoTracking().SingleOrDefaultAsync(ct);
                    sequence = newest?.Sequence ?? 0;
                }
            }
            result.Add(new(witness.TaskId, available, available ? task!.State : null, available ? task!.Revision : 0, sequence));
        }
        return result;
    }
}
