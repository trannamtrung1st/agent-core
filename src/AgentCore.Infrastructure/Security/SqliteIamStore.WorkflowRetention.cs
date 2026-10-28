using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    // Same incumbent deleted-Session transaction as Run/Task minimization. No background execution actor.
    private static async Task RetireWorkflowContent(AgentCoreDbContext db, Guid tenant, DateTimeOffset at, CancellationToken ct)
    {
        var rows = await db.Set<WorkflowExecutionRow>().FromSqlInterpolated($"""
            SELECT e.* FROM TenantWorkflowExecutions e JOIN Sessions s
                ON lower(s.SessionId)=lower(json_extract(e.Json,'$.Source.SessionId'))
            WHERE e.TenantId={tenant} AND e.Status>=8 AND s.DurablyDeletedAtUtc IS NOT NULL
                AND json_extract(e.Json,'$.ContentRetiredAt') IS NULL
                AND NOT EXISTS(SELECT 1 FROM TenantDelegatedTasks t WHERE t.TenantId=e.TenantId AND t.WorkflowExecutionId=e.ExecutionId AND t.State<4)
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=e.TenantId
                    AND (r.SessionId=s.SessionId OR EXISTS(SELECT 1 FROM TenantDelegatedTasks t WHERE t.TenantId=e.TenantId
                        AND t.WorkflowExecutionId=e.ExecutionId AND lower(t.RootActivationId)=lower(r.RootActivationId)))
                    AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
            ORDER BY e.ExecutionId LIMIT 100
            """).ToArrayAsync(ct);
        foreach (var row in rows)
        {
            var before = WorkflowPersistence.Read<WorkflowExecution>(row.Json);
            db.Entry(row).CurrentValues.SetValues(WorkflowPersistence.Row(before with { InputsJson = "{}", ContentRetiredAt = at, Revision = checked(before.Revision + 1) }));
            foreach (var decision in await db.Set<WorkflowDecisionRow>().Where(d => d.TenantId == tenant && d.ExecutionId == row.ExecutionId).ToArrayAsync(ct))
                decision.Json = WorkflowPersistence.Write(WorkflowPersistence.Read<WorkflowProcessDecision>(decision.Json) with { Rationale = null, ContentRetiredAt = at });
        }
    }
}
