using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantRuntimeStateStore
{
    private static IQueryable<SessionRecord> StateSessions(AgentCoreDbContext db, TenantRuntimeStateQuery query)
    {
        var tenant = query.Owner.TenantId!.Value; var instance = query.Owner.AgentInstanceId.ToString("D");
        var executor = query.Owner.ExecutorPrincipalId!.Value; var allowed = query.Sessions?.Select(id => id.ToString("D")).ToArray();
        return db.Sessions.AsNoTracking().Where(s => s.AgentInstanceId == instance && s.ArchivedAtUtc == null && s.DurablyDeletedAtUtc == null
            && s.Snapshot!.ProfileId == null && (allowed == null || allowed.Contains(s.SessionId))
            && db.Set<TenantSessionRecord>().Any(o => o.TenantId == tenant && o.AgentInstanceId == instance
                && o.ExecutorPrincipalId == executor && o.SessionId == s.SessionId));
    }
    public async ValueTask<DurableSessionState> ReadTenantSessionStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default)
    {
        query.Validate(); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var groups = await StateSessions(db, query).GroupBy(s => s.Snapshot!.LifecycleStatus ?? (s.Status == "Ended" ? "Ended" : s.Status == "Paused" ? "Paused" : "Active"))
            .Select(g => new { Status = g.Key, Count = g.Count(), UpdatedAt = g.Max(s => s.UpdatedAtUtc) }).ToArrayAsync(ct).ConfigureAwait(false);
        return new(groups.Where(g => g.Status == "Active").Sum(g => g.Count), groups.Where(g => g.Status == "Paused").Sum(g => g.Count),
            groups.Where(g => g.Status is "Completed" or "Expired" or "Cancelled" or "Ended").Sum(g => g.Count),
            groups.Length == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(groups.Max(g => g.UpdatedAt)));
    }
    public async ValueTask<DurableRunState> ReadTenantRunStateAsync(TenantRuntimeStateQuery query, CancellationToken ct = default)
    {
        query.Validate(); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tenant = query.Owner.TenantId!.Value; var instance = query.Owner.AgentInstanceId.ToString("D");
        var executor = query.Owner.ExecutorPrincipalId!.Value; var allowed = query.Runs?.Select(id => id.ToString("D")).ToArray();
        var sessions = StateSessions(db, query);
        var groups = await OwnedRuns(db, tenant).AsNoTracking().Where(r => r.AgentInstanceId == instance && r.ExecutorPrincipalId == executor
            && (allowed == null || allowed.Contains(r.AgentRunId)) && sessions.Any(s => s.SessionId == r.SessionId))
            .GroupBy(r => r.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToArrayAsync(ct).ConfigureAwait(false);
        int Count(AgentRunStatus status) => groups.Where(g => g.Status == (int)status).Sum(g => g.Count);
        return new(Count(AgentRunStatus.Queued), Count(AgentRunStatus.Running), Count(AgentRunStatus.WaitingForApproval),
            Count(AgentRunStatus.WaitingForSignal), Count(AgentRunStatus.WaitingToRetry), Count(AgentRunStatus.Completed) + Count(AgentRunStatus.Failed) + Count(AgentRunStatus.Cancelled));
    }
    public async ValueTask<ResourceStateObservation> ReadTenantSandboxStateAsync(TenantRuntimeStateQuery query, DateTimeOffset at, CancellationToken ct = default)
    {
        query.Validate(); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tenant = query.Owner.TenantId!.Value; var instance = query.Owner.AgentInstanceId.ToString("D"); var executor = query.Owner.ExecutorPrincipalId!.Value;
        var profiles = query.SandboxProfiles; var sessions = StateSessions(db, query);
        // Filter actual ownership and readable profile ids before materializing bounded lease metadata. Never read execution stdout/stderr.
        var rows = await db.Set<SandboxLeaseRow>().AsNoTracking().Where(l => l.TenantId == tenant && l.AgentInstanceId == instance
            && l.ExecutorPrincipalId == executor && l.State != (int)SandboxLeaseState.Terminated
            && OwnedSandboxProfiles(db, tenant).Any(p => p.Id == l.ProfileId && p.Revision == l.ProfileRevision)
            && (profiles == null || profiles.Contains(l.ProfileId!)) && sessions.Any(s => s.SessionId == l.SessionId))
            .OrderBy(l => l.SessionId).Take(101).Select(l => l.Json).ToArrayAsync(ct).ConfigureAwait(false);
        var leases = rows.Take(100).Select(json => System.Text.Json.JsonSerializer.Deserialize<SandboxLease>(json, SandboxRuntimePersistence.Json)!).ToArray();
        return SandboxStateProjection.Observe(leases, at, rows.Length > 100);
    }
}
