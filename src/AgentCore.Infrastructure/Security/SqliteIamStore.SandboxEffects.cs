using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<AgentCore.Application.Ports.TenantSandboxExecutionFacts?> ReadTenantSandboxExecutionFactsAsync(Guid tenant, Guid session, Guid executionId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == session.ToString("D") && e.Id == executionId.ToString("D"), ct).ConfigureAwait(false);
        if (root is null || row is null || row.ExecutorPrincipalId != root.ExecutorPrincipalId || row.AgentInstanceId != root.AgentInstanceId) return null;
        var lease = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == root.SessionId, ct).ConfigureAwait(false);
        var run = await RetainedTenantOperationRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == row.AgentRunId, ct).ConfigureAwait(false);
        var cancelled = run is not null && AgentRunStoreMapping.ToDomain(run).CancellationRequested;
        return new(root.Domain(), lease is null ? null : SandboxRuntimePersistence.Value(lease), SandboxRuntimePersistence.Value(row), cancelled);
    }
    public async ValueTask<TenantOperationReceipt?> TrySettleApprovedSandboxExecutionAsync(Guid tenant, Guid session, Guid executionId,
        long expectedRevision, AuthorizationAudit audit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == session.ToString("D") && e.Id == executionId.ToString("D"), ct).ConfigureAwait(false);
        if (row?.Revision != expectedRevision) return null;
        var execution = SandboxRuntimePersistence.Value(row);
        if (execution.ApprovalRequestId is not { } requestId || execution.OperationId is not { } operation) return null;
        var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == requestId, ct).ConfigureAwait(false))?.Domain();
        var runRow = await RetainedTenantOperationRuns(db, tenant).SingleOrDefaultAsync(r => r.AgentRunId == execution.AgentRunId.ToString("D"), ct).ConfigureAwait(false);
        if (request is null || runRow is null) return null;
        var run = AgentRunStoreMapping.ToDomain(runRow);
        TenantSandboxEffectRules.Require(request, execution, run);
        var old = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.OperationId == operation, ct).ConfigureAwait(false);
        if (old is not null) return old.Domain();
        if (TenantSandboxEffectRules.Outcome(execution) is null) return null;
        var at = ApprovalNow;
        var receipt = TenantSandboxEffectRules.Receipt(request, execution, run, at);
        var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        var next = TenantSandboxEffectRules.SettleRun(run, execution, receipt.Outcome, at);
        audit = TenantSandboxEffectRules.Audit(audit, state, run, request, receipt);
        if (next.Revision != run.Revision) AgentRunStoreMapping.Apply(runRow, next);
        db.Add(TenantOperationReceiptRecord.From(receipt)); db.Add(audit);
        return await SaveSandbox(db, tx, ct).ConfigureAwait(false) ? receipt : null;
    }
}
