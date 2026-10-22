using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private static IQueryable<TenantApprovalRecord> AutomationMutationVisible(AgentCoreDbContext db, IQueryable<TenantApprovalRecord> query, TenantAutomationApprovalVisibility? visibility)
    {
        if (visibility is null) return query.Where(r => db.Set<TenantApprovalRecord>().FromSqlRaw("SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.AutomationMutation') IS NOT 'object'").Any(v => v.RequestId == r.RequestId));
        static string Json(IReadOnlyList<Guid>? values) => System.Text.Json.JsonSerializer.Serialize(values?.Select(id => id.ToString("D")));
        var agents = Json(visibility.Agents); var parents = Json(visibility.ReadAutomations); var webhooks = Json(visibility.WebhookSources); var core = visibility.CoreEvents is null;
        var sessions = Json(visibility.ConversationSessions); var chats = Json(visibility.ChatAgents);
        var rows = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests request WHERE json_type(PayloadJson,'$.Operation.AutomationMutation') IS NOT 'object'
            OR ((json_type({agents})='null' OR InstanceId IN (SELECT value FROM json_each({agents})))
                AND (json_type(PayloadJson,'$.Operation.AutomationMutation.Current') IS NOT 'object' OR
                    ((json_type({parents})='null' OR ResourceId IN (SELECT value FROM json_each({parents})))
                        AND EXISTS (SELECT 1 FROM Automations parent WHERE parent.TenantId=request.TenantId AND parent.AgentInstanceId=request.InstanceId
                            AND parent.ExecutorPrincipalId=request.ExecutorPrincipalId AND parent.AutomationId=request.ResourceId)))
                AND (json_extract(PayloadJson,'$.Operation.AutomationMutation.ToolName') IN ('automation.disable','automation.delete') OR
                    (NOT EXISTS (SELECT 1 FROM json_each(PayloadJson,'$.Operation.AutomationMutation.Proposed.Triggers') child
                        WHERE (json_type(child.value,'$.WebhookId')='text' AND json_type({webhooks})<>'null' AND json_extract(child.value,'$.WebhookId') NOT IN (SELECT value FROM json_each({webhooks})))
                            OR (json_type(child.value,'$.CoreEventKey')='text' AND NOT {core}))
                    AND (json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.TargetSessionId') IS NULL OR
                        ((json_type({sessions})='null' OR json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.TargetSessionId') IN (SELECT value FROM json_each({sessions})))
                            AND (json_type({chats})='null' OR InstanceId IN (SELECT value FROM json_each({chats})))
                            AND EXISTS (SELECT 1 FROM TenantSessions s WHERE s.TenantId=request.TenantId AND s.AgentInstanceId=request.InstanceId AND s.ExecutorPrincipalId=request.ExecutorPrincipalId
                                AND s.SessionId=json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.TargetSessionId'))))
                    AND (json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.ReportToSessionId') IS NULL OR
                        ((json_type({sessions})='null' OR json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.ReportToSessionId') IN (SELECT value FROM json_each({sessions})))
                            AND (json_type({chats})='null' OR InstanceId IN (SELECT value FROM json_each({chats})))
                            AND EXISTS (SELECT 1 FROM TenantSessions s WHERE s.TenantId=request.TenantId AND s.AgentInstanceId=request.InstanceId AND s.ExecutorPrincipalId=request.ExecutorPrincipalId
                                AND s.SessionId=json_extract(PayloadJson,'$.Operation.AutomationMutation.Proposed.ReportToSessionId')))))))
            """);
        return query.Where(r => rows.Any(v => v.RequestId == r.RequestId));
    }
}
