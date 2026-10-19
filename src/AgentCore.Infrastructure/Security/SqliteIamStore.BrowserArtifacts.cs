using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static async ValueTask<(int Captures, int Downloads)> BrowserArtifactUsage(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct)
    {
        var rows = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == request.TenantId
            && r.RootActivationId == request.RootActivationId.ToString("D") && r.ExecutorPrincipalId == request.ExecutorPrincipalId
            && r.SessionId == request.SessionId.ToString("D") && r.BrowserArtifactsJson != null).ToArrayAsync(ct).ConfigureAwait(false);
        return TenantBrowserArtifactRules.Usage(rows.Select(r => r.Domain()));
    }
    private static async ValueTask RequireBrowserArtifactBudget(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct)
    {
        if (request.Operation.BrowserCommand?.Artifacts is not { } policy) return;
        var usage = await BrowserArtifactUsage(db, request, ct).ConfigureAwait(false);
        var pending = await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT a.* FROM TenantApprovalRequests a JOIN AgentRuns r ON r.TenantId = a.TenantId AND lower(a.RequestId) = json_extract(r.PayloadJson, '$.approvalRequestId')
            WHERE a.TenantId = {request.TenantId} AND a.RootActivationId = {request.RootActivationId.ToString("D")}
                AND a.ExecutorPrincipalId = {request.ExecutorPrincipalId} AND a.SessionId = {request.SessionId.ToString("D")}
                AND json_type(r.PayloadJson, '$.sideEffect.externalEffect') IS 'object' AND json_extract(r.PayloadJson, '$.sideEffect.disposition') = 2
                AND NOT EXISTS (SELECT 1 FROM TenantOperationReceipts receipt WHERE receipt.OperationId = a.OperationId)
            """).AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in pending)
            if (row.Domain().Operation.BrowserCommand?.Artifacts is { } reserved)
            { if (reserved.Capture) usage.Captures++; if (reserved.Downloads) usage.Downloads++; }
        TenantBrowserArtifactRules.RequireBudget(policy, usage);
    }
    private async ValueTask<string> PublishBrowserArtifacts(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, TenantBrowserArtifactBatch batch, CancellationToken ct)
    {
        if (artifactBacking is null) return TenantBrowserArtifactRules.Unavailable();
        var records = new List<ArtifactRecordRow>();
        try
        {
            var usage = await BrowserArtifactUsage(db, request, ct).ConfigureAwait(false);
            batch = TenantBrowserArtifactRules.Bound(request.Operation.BrowserCommand!, request.Operation.OperationId, batch, usage.Captures, usage.Downloads);
            var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, run.Claim!.Generation);
            await artifactBacking.RequireBrowserBatchCapacityAsync(db, frame, batch.Files, ct).ConfigureAwait(false);
            foreach (var file in batch.Files)
            {
                var frozen = file.Content.ToArray(); var record = TenantArtifactRules.Record(frame, file.ArtifactId, file.DisplayName, file.ContentType, frozen, ApprovalNow);
                var row = await artifactBacking.StageTenantLocalAsync(db, frame, record, frozen, ct).ConfigureAwait(false);
                records.Add(row); db.Artifacts.Add(row);
            }
            var expires = request.ExpiresAt;
            if ((await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false))?.ExpiresAt is { } grantExpiry
                && grantExpiry < expires) expires = grantExpiry;
            if (expires <= ApprovalNow || run.Claim!.LeaseExpiresAtUtc <= ApprovalNow) throw new AccessDeniedException("execution_claim_expired");
            // Metadata remains private to this transaction; receipt source guards can now verify it.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return TenantBrowserArtifactRules.Result(batch.Files, batch.UnavailableReason);
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or IOException or UnauthorizedAccessException
            or AgentCore.Application.Sessions.AgentCoreException or DbUpdateException)
        {
            foreach (var row in records) db.Entry(row).State = EntityState.Detached;
            return TenantBrowserArtifactRules.Unavailable(e is AgentCore.Application.Sessions.AgentCoreException ? "artifact_quota_exceeded" : "artifact_publication_unavailable");
        }
    }
}
