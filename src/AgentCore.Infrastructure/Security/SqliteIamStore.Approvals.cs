using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantApprovalStore
{
    private DateTimeOffset ApprovalNow => (clock ?? TimeProvider.System).GetUtcNow();
    public async ValueTask<TenantApprovalRequest?> FindApprovalAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<TenantApprovalRequest?> FindRootApprovalAsync(Guid tenantId, Guid rootId, Guid executorId, PreparedTenantOperation operation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await RootApproval(db, tenantId, rootId.ToString("D"), executorId, operation).AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false))?.Domain();
    }
    private static IQueryable<TenantApprovalRecord> RootApproval(AgentCoreDbContext db, Guid tenant, string root, Guid executor, PreparedTenantOperation operation) =>
        SemanticApprovals(db, operation).Where(r => r.TenantId == tenant && r.RootActivationId == root && r.ExecutorPrincipalId == executor)
            .OrderBy(r => r.Status == (int)TenantApprovalStatus.Rejected || r.Status == (int)TenantApprovalStatus.Expired || r.Status == (int)TenantApprovalStatus.Cancelled ? 0 : r.Status == (int)TenantApprovalStatus.Pending ? 1 : 2)
            .ThenBy(r => r.CreatedAt).ThenBy(r => r.RequestId);
    private static IQueryable<TenantApprovalRecord> SemanticApprovals(AgentCoreDbContext db, PreparedTenantOperation operation) =>
        db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests
            WHERE json_remove(json_extract(PayloadJson, '$.Operation.CanonicalActionJson'), '$.expectedInstanceRevision', '$.expectedPersonaRevision') = {operation.SemanticActionJson}
            """);
    public async ValueTask<IReadOnlyList<TenantApprovalRequest>> ListApprovalsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedRequests, IReadOnlyList<Guid>? allowedInstances, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == tenantId);
        if (allowedRequests is not null) query = query.Where(r => allowedRequests.Contains(r.RequestId));
        if (allowedInstances is not null)
        { var ids = allowedInstances.Select(id => id.ToString("D")).ToArray(); query = query.Where(r => ids.Contains(r.InstanceId)); }
        return (await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.RequestId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToArray();
    }
    public async ValueTask<TenantOperationGrant?> FindOperationGrantAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantApprovalRequest?> TryCreateApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantApprovalRequest request, long expectedRunRevision, Guid? generation, AuthorizationAudit audit, CancellationToken ct = default)
    {
        if (request.TenantId != next.Tenant.TenantId || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "approvals.request"
            || audit.Target != request.RequestId.ToString("D")) throw new ArgumentException("Invalid approval request transaction.");
        TenantApprovalRequest? result = null;
        var written = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var duplicate = await RootApproval(db, request.TenantId, request.RootActivationId.ToString("D"), request.ExecutorPrincipalId, request.Operation).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (duplicate is not null) { result = duplicate.Domain(); return false; }
            if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
            var row = await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
            var target = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(r => r.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || target is null || row.Revision != expectedRunRevision) return false;
            var run = AgentRunStoreMapping.ToDomain(row); var agent = Owned(target);
            TenantApprovalRules.Validate(request, run, agent);
            var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
            TenantSessionRules.RequireExecutor(state, agent);
            if (request.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, state, requester, ct).ConfigureAwait(false);
            var cutoff = (ApprovalNow - TenantApprovalLimits.Window).ToUnixTimeMilliseconds();
            var recent = db.Set<TenantApprovalRecord>().Where(r => r.TenantId == request.TenantId
                && (r.Status == (int)TenantApprovalStatus.Pending || r.CreatedAt >= cutoff));
            var counts = await recent.GroupBy(r => r.TenantId).Select(g => new { Tenant = g.Count(),
                Executor = g.Count(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId),
                Requester = g.Count(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId) }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            var operationCount = await SemanticApprovals(db, request.Operation).CountAsync(r => r.TenantId == request.TenantId
                && (r.Status == (int)TenantApprovalStatus.Pending || r.CreatedAt >= cutoff), ct).ConfigureAwait(false);
            var rootCount = await db.Set<TenantApprovalRecord>().CountAsync(r => r.TenantId == request.TenantId
                && r.RootActivationId == request.RootActivationId.ToString("D"), ct).ConfigureAwait(false);
            TenantApprovalLimits.Require(counts?.Tenant ?? 0, counts?.Executor ?? 0, counts?.Requester ?? 0, operationCount, rootCount);
            if (run.Status == AgentRunStatus.Running && run.Claim!.LeaseExpiresAtUtc <= ApprovalNow) throw new SecurityConflictException("execution_claim_expired");
            var saved = run.WaitForDurableApproval(expectedRunRevision, request.RequestId, generation, request.CreatedAt);
            AgentRunStoreMapping.Apply(row, saved); db.Add(TenantApprovalRecord.From(request)); result = request; return true;
        }, ct).ConfigureAwait(false);
        return written || result is not null && result.RequestId != request.RequestId ? result : null;
    }

    public ValueTask<bool> TryDecideApprovalAsync(TenantSecurityState next, long expectedSecurityRevision, TenantApprovalRequest request,
        long expectedRequestRevision, TenantApprovalDecision decision, TenantOperationGrant? grant, ApprovalProcessedSignal signal,
        TenantSessionInitiator human, AuthorizationAudit audit, CancellationToken ct = default) => WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
    {
        if (request.TenantId != next.Tenant.TenantId) throw new ArgumentException("Invalid approval decision transaction.");
        await RequireHumanSessionAsync(db, request.TenantId, human, ct).ConfigureAwait(false);
        var row = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.RequestId == request.RequestId, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedRequestRevision || row.Status != (int)TenantApprovalStatus.Pending) return false;
        TenantApprovalRules.ValidateDecision(row.Domain(), request, expectedRequestRevision, decision, grant, signal, human, audit);
        var runRow = await OwnedRuns(db, request.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId.ToString("D"), ct).ConfigureAwait(false);
        var target = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(r => r.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (runRow is null || target is null) return false;
        var run = AgentRunStoreMapping.ToDomain(runRow); var agent = Owned(target);
        if (run.ApprovalRequestId != request.RequestId || run.Status != AgentRunStatus.WaitingForApproval) return false;
        TenantApprovalRules.Validate(request, run, agent, request.Status != TenantApprovalStatus.Cancelled);
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!;
        if (request.Status != TenantApprovalStatus.Cancelled)
        {
            TenantSessionRules.RequireExecutor(state, agent); await ValidateTenantActorAsync(db, state, request.ExecutorPrincipalId, ct).ConfigureAwait(false);
            if (request.OriginalInitiatorPrincipalId is { } requester) await ValidateTenantActorAsync(db, state, requester, ct).ConfigureAwait(false);
        }
        if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
        db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(request)); db.Add(decision); db.Add(signal);
        if (grant is not null) db.Add(grant); return true;
    }, ct);

    public async ValueTask<IReadOnlyList<ApprovalProcessedSignal>> PendingApprovalSignalsAsync(int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<ApprovalProcessedSignal>().AsNoTracking().Where(s => s.ProcessedAt == null).OrderBy(s => s.CreatedAt).ThenBy(s => s.SignalId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TryResumeApprovalAsync(ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default, TenantApprovalContinuationIds? continuation = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct).ConfigureAwait(false);
        if (saved != signal || saved is null || saved.ProcessedAt is not null || at < saved.CreatedAt) return false;
        var request = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct).ConfigureAwait(false);
        if (request is null || request.Revision != signal.DecisionRevision || request.Status == (int)TenantApprovalStatus.Pending || request.RunId != signal.RunId.ToString("D")) return false;
        var row = await OwnedRuns(db, signal.TenantId).SingleOrDefaultAsync(r => r.AgentRunId == request.RunId, ct).ConfigureAwait(false);
        if (row is null) return false;
        var run = AgentRunStoreMapping.ToDomain(row);
        if (run.ApprovalRequestId != signal.RequestId) return false;
        if (!run.IsTerminal)
        {
            if (run.Status != AgentRunStatus.WaitingForApproval) return false;
            if (run.Checkpoint is not null)
            {
                if (continuation is null) return false;
                var next = TenantApprovalContinuationRules.Create(run, request.Domain(), signal, continuation, at);
                var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.SessionId == row.SessionId, ct).ConfigureAwait(false);
                db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(SqliteMemoryStore.ToSnapshot(session, []), next));
                db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(next));
                db.Add(new AuthorizationAudit(signal.SignalId, signal.TenantId, request.ExecutorPrincipalId, "agent-runs.approval-continuation", next.AgentRunId.ToString("D"), "allowed", "approval_continued", at));
                AgentRunStoreMapping.Apply(row, run.HandoffApprovalContinuation(run.Revision, signal.RequestId, next.AgentRunId, at));
            }
            else AgentRunStoreMapping.Apply(row, run.ResumeDurableApproval(run.Revision, signal.RequestId, at));
        }
        db.Entry(saved).CurrentValues.SetValues(saved with { Revision = checked(saved.Revision + 1), ProcessedAt = at });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
    public async ValueTask<IReadOnlyList<TenantApprovalRequest>> DueApprovalsAsync(DateTimeOffset at, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var millis = at.ToUnixTimeMilliseconds();
        return (await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.Status == (int)TenantApprovalStatus.Pending && r.ExpiresAt <= millis)
            .OrderBy(r => r.ExpiresAt).ThenBy(r => r.RequestId).Take(Math.Clamp(limit, 1, 100)).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToArray();
    }
    public async ValueTask<bool> TryExpireApprovalAsync(Guid tenantId, Guid requestId, long expectedRevision, ApprovalProcessedSignal signal, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.RequestId == requestId, ct).ConfigureAwait(false);
        if (row is null || row.Revision != expectedRevision || row.Status != (int)TenantApprovalStatus.Pending) return false;
        var request = row.Domain(); if (request.ExpiresAt > at) return false;
        db.Entry(row).CurrentValues.SetValues(TenantApprovalRecord.From(TenantApprovalRules.Expire(request, expectedRevision, signal, at))); db.Add(signal);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { return false; }
    }
}
