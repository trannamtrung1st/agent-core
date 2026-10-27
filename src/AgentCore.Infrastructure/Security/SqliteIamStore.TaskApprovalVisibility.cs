using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private sealed class TaskApprovalSourceRecord { public Guid RequestId { get; set; } public string SourceSessionId { get; set; } = ""; }
    private static IQueryable<TaskApprovalSourceRecord> TaskApprovalSources(AgentCoreDbContext db, Guid tenant) => db.Database.SqlQuery<TaskApprovalSourceRecord>($"SELECT RequestId, json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId') AS SourceSessionId FROM TenantApprovalRequests WHERE TenantId={tenant} AND json_type(PayloadJson,'$.Operation.TaskDelegation')='object'");
    private IQueryable<TenantApprovalRecord> TaskApprovalVisibility(AgentCoreDbContext db, Guid tenant,
        IQueryable<TenantApprovalRecord> query, TenantTaskApprovalVisibility? visibility)
    {
        var taskRequests = db.Set<TenantApprovalRecord>().FromSqlRaw("SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson,'$.Operation.TaskDelegation')='object'");
        if (visibility is not { CanCreate: true, ModelsReadable: true }) return query.Where(r => !taskRequests.Any(t => t.RequestId == r.RequestId));
        static string Ids(IReadOnlyList<Guid>? ids) => JsonSerializer.Serialize(ids?.Select(id => id.ToString("D")).ToArray());
        var agents = Ids(visibility.Agents); var sessions = Ids(visibility.Sessions); var delegates = Ids(visibility.DelegateSessions);
        var spaces = Ids(visibility.Spaces); var environments = Ids(visibility.EnvironmentSpaces);
        var visible = db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE TenantId={tenant} AND json_type(PayloadJson,'$.Operation.TaskDelegation')='object'
                AND ({agents}='null' OR (InstanceId IN (SELECT value FROM json_each({agents}))
                    AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceId') IN (SELECT value FROM json_each({agents}))))
                AND ({sessions}='null' OR json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId') IN (SELECT value FROM json_each({sessions})))
                AND ({delegates}='null' OR json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId') IN (SELECT value FROM json_each({delegates})))
                AND ({spaces}='null' OR json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.SpaceId') IN (SELECT value FROM json_each({spaces})))
                AND (json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.EnvironmentId') IS NULL OR {environments}='null'
                    OR json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.SpaceId') IN (SELECT value FROM json_each({environments})))
            """);
        var retained = OwnedSessions(db, tenant); var sources = TaskApprovalSources(db, tenant);
        return query.Where(r => !taskRequests.Any(t => t.RequestId == r.RequestId)
            || visible.Any(v => v.RequestId == r.RequestId) && sources.Any(a => a.RequestId == r.RequestId && retained.Any(s => s.SessionId == a.SourceSessionId)));
    }
}
