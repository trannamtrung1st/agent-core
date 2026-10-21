using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask RequireTenantExperienceSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantExperienceSourceReference> sources,
        IReadOnlyList<TenantExperienceFactSource> facts, Guid? generation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        await RequireExperienceSources(db, run, sources, facts, ct).ConfigureAwait(false);
    }
    private async Task RequireExperienceSources(AgentCoreDbContext db, AgentRun run, IReadOnlyList<TenantExperienceSourceReference> sources, IReadOnlyList<TenantExperienceFactSource> facts, CancellationToken ct)
    {
        TenantExperienceCommitRules.SourcesShape(run, sources, facts); if (sources.Count == 0 && facts.Count == 0) return;
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        foreach (var source in sources)
        {
            var observed = await ObserveExperienceSource(db, run, source.Witness.Kind, source.Witness.SourceId, source.Witness, null, ct).ConfigureAwait(false);
            if (!await TenantExperienceRules.OrdinaryAsync(reader, observed.Witness, false, false, iam, ct).ConfigureAwait(false)
                && !await ExperienceSourceConsented(db, observed, ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_read_review_required");
        }
        if (facts.Count == 0) return;
        await TenantExperienceRules.RawAsync(reader, null, true, iam, ct).ConfigureAwait(false);
        var ordinary = await TenantExperienceRules.OrdinaryAsync(reader, null, true, false, iam, ct).ConfigureAwait(false);
        var ids = facts.Select(i => i.ExperienceId).ToArray(); var keys = new[] { "instance", TenantExperienceRules.OwnerKey(reader) };
        var items = await db.Set<TenantExperienceRecord>().AsNoTracking().Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.AgentInstanceId == run.AgentInstanceId.ToString("D") && keys.Contains(i.OwnerKey) && ids.Contains(i.ExperienceId))
            .Select(i => new TenantExperienceReference(i.TenantId, i.ExperienceId, Guid.Parse(i.AgentInstanceId), i.ExecutorPrincipalId, i.UserPrincipalId, i.AccountId, i.UserId, i.ContentHash, (ExperienceVisibility)i.Visibility, i.Revision)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var source in facts)
        {
            var item = items.SingleOrDefault(i => i.ExperienceId == source.ExperienceId);
            if (item is null || !TenantExperienceRules.Owns(reader, item)) throw new AccessDeniedException("experience_source_changed");
            var ownConsolidation = !TenantExperienceCommitRules.FactMatches(source, item) && await OwnExperienceConsolidation(db, reader, settings, source, item, [], ct).ConfigureAwait(false);
            if (!TenantExperienceCommitRules.FactMatches(source, item) && !ownConsolidation
                || !ordinary && !ownConsolidation && !await ExperienceFactConsented(db, reader, settings, source, ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_source_changed");
        }
    }
    private async Task<(TenantApprovalRequest Request, Guid Human)[]> ExperienceReadReceipts(AgentCoreDbContext db, TenantMemoryReader reader, string reason, CancellationToken ct)
    {
        var rows = await (from receipt in db.Set<TenantOperationReceiptRecord>().AsNoTracking()
            join approval in db.Set<TenantApprovalRecord>().AsNoTracking() on receipt.RequestId equals (Guid?)approval.RequestId
            join decision in db.Set<TenantApprovalDecision>().AsNoTracking() on approval.RequestId equals decision.RequestId
            where receipt.TenantId == reader.Security.Tenant.TenantId && approval.TenantId == receipt.TenantId && decision.TenantId == receipt.TenantId
                && receipt.SessionId == reader.Run.SessionId.ToString("D") && receipt.RootActivationId == reader.Run.Admission.Activation.RootActivationId.ToString("D")
                && receipt.Outcome == (int)TenantOperationOutcome.Completed && receipt.ReasonCode == reason && decision.Status == TenantApprovalStatus.Approved
            select new { approval.PayloadJson, decision.HumanPrincipalId }).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(r => (JsonSerializer.Deserialize<TenantApprovalRequest>(r.PayloadJson)!, r.HumanPrincipalId)).ToArray();
    }
    private async Task<bool> ExperienceSourceConsented(AgentCoreDbContext db, TenantExperienceSourceObservation observed, CancellationToken ct)
    {
        foreach (var (request, human) in await ExperienceReadReceipts(db, observed.Reader, TenantExperienceRules.ReadCompleted, ct).ConfigureAwait(false))
        {
            if (request.Operation.ExperienceOperation?.Source != observed.Witness) continue;
            try { await ValidateTenantActorAsync(db, observed.Reader.Security, human, ct).ConfigureAwait(false); await TenantExperienceConsentRules.SourceAsync(request, observed, human, new IamAuthorizer(this), ct).ConfigureAwait(false); return true; }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private async Task<bool> ExperienceFactConsented(AgentCoreDbContext db, TenantMemoryReader reader, TenantIsolationMetadata settings, TenantExperienceFactSource source, CancellationToken ct)
    {
        foreach (var (request, human) in await ExperienceReadReceipts(db, reader, TenantContinuityRules.Completed, ct).ConfigureAwait(false))
        {
            if (request.Operation.ContinuityRead?.Experience is not { } value || !TenantExperienceFactRules.Sources(value).Any(i => TenantExperienceCommitRules.FactMatches(source, i))) continue;
            try
            {
                var observed = await RequireContinuityOperation(db, request, reader.Run, ct).ConfigureAwait(false);
                await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
                await TenantContinuityReadRules.ReviewerAsync(request, observed, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                var ids = TenantExperienceFactRules.Sources(value).Select(i => i.ExperienceId).ToArray();
                var items = await db.Set<TenantExperienceRecord>().AsNoTracking().Where(i => i.TenantId == request.TenantId && ids.Contains(i.ExperienceId))
                    .Select(i => new TenantExperienceReference(i.TenantId, i.ExperienceId, Guid.Parse(i.AgentInstanceId), i.ExecutorPrincipalId, i.UserPrincipalId, i.AccountId, i.UserId, i.ContentHash, (ExperienceVisibility)i.Visibility, i.Revision)).ToArrayAsync(ct).ConfigureAwait(false);
                await TenantExperienceConsentRules.FactsAsync(request, reader, settings, items, human, new IamAuthorizer(this), ct).ConfigureAwait(false); return true;
            }
            catch (AccessDeniedException) { }
        }
        return false;
    }
    private async Task<bool> OwnExperienceConsolidation(AgentCoreDbContext db, TenantMemoryReader reader, TenantIsolationMetadata settings,
        TenantExperienceFactSource source, TenantExperienceReference item, HashSet<Guid> visited, CancellationToken ct)
    {
        if (item.Visibility != ExperienceVisibility.Superseded || item.Revision != source.Revision + 1 || item.ContentHash != source.ContentHash || visited.Count >= 100 || !visited.Add(item.ExperienceId)) return false;
        foreach (var (request, human) in await ExperienceReadReceipts(db, reader, TenantExperienceRules.Consolidated, ct).ConfigureAwait(false))
        {
            if (request.Operation.ExperienceOperation is not { ToolName: TenantExperienceToolContract.Consolidate, ResultExperienceId: { } resultId } value
                || !TenantExperienceRules.Sources(value).Any(i => i.ExperienceId == source.ExperienceId && i.ContentHash == source.ContentHash && i.Revision == source.Revision)) continue;
            var result = (await db.Set<TenantExperienceRecord>().AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == reader.Security.Tenant.TenantId && i.ExperienceId == resultId, ct).ConfigureAwait(false))?.Domain();
            if (result is null) continue;
            try
            {
                await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
                await TenantExperienceConsentRules.OwnConsolidationAsync(request, reader, settings, source, item, result, human, new IamAuthorizer(this), ct).ConfigureAwait(false);
                if (result.Visibility == ExperienceVisibility.Eligible && result.Revision == 1 || await OwnExperienceConsolidation(db, reader, settings,
                    new(result.TenantId, result.ExperienceId, result.ContentHash, 1), result.Reference(), new(visited), ct).ConfigureAwait(false)) return true;
            }
            catch (Exception error) when (error is AccessDeniedException or SecurityConflictException) { }
        }
        return false;
    }
    private async Task RequireExperienceCheckpoint(AgentCoreDbContext db, AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        var sources = TenantExperienceReferences.Sources(checkpoint); var facts = TenantExperienceReferences.Facts(checkpoint);
        if (TenantExperienceReferences.Sources(run.Checkpoint).Except(sources).Any() || TenantExperienceReferences.Facts(run.Checkpoint).Except(facts).Any()) throw new AccessDeniedException("experience_source_unavailable");
        await RequireExperienceSources(db, run, sources, facts, ct).ConfigureAwait(false);
    }
}
