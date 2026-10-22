using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask RequireTenantManagementReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantManagementReadReference> sources, Guid? generation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("management_source_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        await RequireManagementReadSources(db, run, sources, ct).ConfigureAwait(false);
    }
    private async Task RequireManagementReadSources(AgentCoreDbContext db, AgentRun run, IReadOnlyList<TenantManagementReadReference> sources, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        if (sources.Count > 64 || sources.Any(s => s.TenantId != run.Owner.TenantId || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("management_source_unavailable");
        var reader = await ManagementReader(db, run, null, ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        foreach (var source in sources)
        {
            var shape = TenantManagementReadReferences.Shape(source, run.SessionId, reader.Root.SessionId);
            var observed = await ObserveManagementRead(db, run, source.ToolName, source.ArgumentsJson, shape, null, ct).ConfigureAwait(false); var review = false;
            review = await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId,
                "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).ConfigureAwait(false);
            if (review && !await ManagementReadConsented(db, reader, source, ct).ConfigureAwait(false)) throw new AccessDeniedException("management_read_review_required");
        }
    }
    private async Task<bool> ManagementReadConsented(AgentCoreDbContext db, TenantManagementReader reader, TenantManagementReadReference source, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var session = reader.Run.SessionId.ToString("D"); var root = reader.Run.Admission.Activation.RootActivationId.ToString("D");
        var receipts = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == tenant && r.SessionId == session && r.RootActivationId == root
            && r.Outcome == (int)TenantOperationOutcome.Completed && r.RequestId != null && r.ReasonCode == TenantManagementReadRules.Completed).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var receipt in receipts)
        {
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == receipt.RequestId, ct).ConfigureAwait(false))?.Domain();
            if (request?.Operation.ManagementRead is not { } value || !TenantManagementReadReferences.Matches(value, source)) continue;
            var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == request.RequestId, ct).ConfigureAwait(false);
            if (decision?.Status != TenantApprovalStatus.Approved) continue;
            try {
                var observed = await ObserveManagementRead(db, reader.Run, value.ToolName, value.ArgumentsJson, value, null, ct).ConfigureAwait(false);
                await ValidateTenantActorAsync(db, reader.Security, decision.HumanPrincipalId, ct).ConfigureAwait(false);
                await TenantManagementReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false); return true;
            } catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private async Task RequireManagementReadCheckpoint(AgentCoreDbContext db, AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var before = TenantManagementReadReferences.Read(run.Checkpoint); var after = TenantManagementReadReferences.Read(checkpoint);
        if (before.Except(after).Any()) throw new AccessDeniedException("management_source_unavailable"); await RequireManagementReadSources(db, run, after, ct).ConfigureAwait(false);
    }
}
