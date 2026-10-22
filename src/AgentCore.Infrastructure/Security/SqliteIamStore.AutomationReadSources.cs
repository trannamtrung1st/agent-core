using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask RequireTenantAutomationReadSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantAutomationReadReference> sources, Guid? generation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("automation_source_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        await RequireAutomationReadSources(db, run, sources, ct).ConfigureAwait(false);
    }
    private async Task RequireAutomationReadSources(AgentCoreDbContext db, AgentRun run, IReadOnlyList<TenantAutomationReadReference> sources, CancellationToken ct)
    {
        if (sources.Count == 0) return;
        if (sources.Count > 64 || sources.Any(s => s.TenantId != run.Owner.TenantId || !TenantMemoryOperationRules.ValidHash(s.ObservationHash))) throw new AccessDeniedException("automation_source_unavailable");
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        foreach (var source in sources)
        {
            var shape = TenantAutomationReadReferences.Shape(source, run.SessionId, reader.Root.SessionId);
            var observed = await ObserveAutomationRead(db, run, source.ToolName, source.ArgumentsJson, shape, null, ct).ConfigureAwait(false); var review = false;
            foreach (var (key, scope) in TenantAutomationReadRules.Targets(reader, observed.Value))
                review |= await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, key, scope, iam, ct).ConfigureAwait(false);
            if (review && !await AutomationReadConsented(db, reader, source, ct).ConfigureAwait(false)) throw new AccessDeniedException("automation_read_review_required");
        }
    }
    private async Task<bool> AutomationReadConsented(AgentCoreDbContext db, TenantMemoryReader reader, TenantAutomationReadReference source, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var session = reader.Run.SessionId.ToString("D"); var root = reader.Run.Admission.Activation.RootActivationId.ToString("D");
        var receipts = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == tenant && r.SessionId == session && r.RootActivationId == root
            && r.Outcome == (int)TenantOperationOutcome.Completed && r.RequestId != null && r.ReasonCode == TenantAutomationReadRules.Completed).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var receipt in receipts)
        {
            var request = (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == receipt.RequestId, ct).ConfigureAwait(false))?.Domain();
            if (request?.Operation.AutomationRead is not { } value || !TenantAutomationReadReferences.Matches(value, source)) continue;
            var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == request.RequestId, ct).ConfigureAwait(false);
            if (decision?.Status != TenantApprovalStatus.Approved) continue;
            try {
                var observed = await ObserveAutomationRead(db, reader.Run, value.ToolName, value.ArgumentsJson, value, null, ct).ConfigureAwait(false);
                await ValidateTenantActorAsync(db, reader.Security, decision.HumanPrincipalId, ct).ConfigureAwait(false);
                await TenantAutomationReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false); return true;
            } catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private async Task RequireAutomationReadCheckpoint(AgentCoreDbContext db, AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var before = TenantAutomationReadReferences.Read(run.Checkpoint); var after = TenantAutomationReadReferences.Read(checkpoint);
        if (before.Except(after).Any()) throw new AccessDeniedException("automation_source_unavailable"); await RequireAutomationReadSources(db, run, after, ct).ConfigureAwait(false);
    }
}
