using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantMemoryOperationStore
{
    public async ValueTask<TenantMemoryOperationObservation> ObserveTenantMemoryOperationAsync(TenantRunReference reference, Guid? generation,
        TenantMemoryArguments arguments, PreparedMemoryOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("memory_context_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveMemoryOperation(db, run, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantMemoryOperationObservation> ObserveMemoryOperation(AgentCoreDbContext db, AgentRun run, TenantMemoryArguments arguments,
        PreparedMemoryOperation? expected, Guid? inspectionHuman, CancellationToken ct)
    {
        var args = TenantMemoryToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        if (inspectionHuman is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        if (inspectionHuman is { } human) await ValidateTenantActorAsync(db, reader.Security, human, ct).ConfigureAwait(false);
        var readable = (await TenantMemoryOperationRules.ReadableAgentsAsync(reader, new IamAuthorizer(this), inspectionHuman, ct).ConfigureAwait(false)).Select(id => id.ToString("D")).ToArray();
        var keys = TenantMemoryOperationRules.Keys(reader, inspectionHuman is not null).ToArray();
        var query = db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == reader.Security.Tenant.TenantId && keys.Contains(i.OwnerKey) && readable.Contains(i.AgentInstanceId));
        if (inspectionHuman is not null)
        {
            var selected = TenantMemoryOperationRules.Sources(expected!).Select(s => s.MemoryId).ToArray(); query = query.Where(i => selected.Contains(i.MemoryId));
        }
        else if (args.SourceIds is not null) { var selected = args.SourceIds.ToArray(); query = query.Where(i => selected.Contains(i.MemoryId)); }
        else if (args.MemoryId is { } id) query = query.Where(i => i.MemoryId == id);
        else query = query.Where(i => i.Status == (int)MemoryItemStatus.Active);
        var rows = await query.OrderBy(i => i.MemoryId).Take(MemoryLimits.MaxActiveItems * 3 + 1).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Length > MemoryLimits.MaxActiveItems * 3) throw new AccessDeniedException("memory_capacity_invalid");
        if (args.Tool == ToolCatalog.MemoryConsolidate && rows.Length > 0 && inspectionHuman is null)
        {
            var resultId = TenantMemoryOperationRules.ResultId(reader, args, rows[0].Domain());
            var result = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == reader.Security.Tenant.TenantId
                && keys.Contains(i.OwnerKey) && readable.Contains(i.AgentInstanceId) && i.MemoryId == resultId, ct).ConfigureAwait(false);
            if (result is not null && !rows.Any(i => i.MemoryId == resultId)) rows = [.. rows, result];
        }
        var observed = TenantMemoryOperationRules.Observe(reader, args, rows.Select(i => i.Domain()).ToArray(), expected, inspectionHuman is not null);
        await TenantMemoryOperationRules.RequireRawAsync(reader, observed.Items, new IamAuthorizer(this), ct, inspectionHuman).ConfigureAwait(false); return observed;
    }
}

public sealed partial class SqliteIamStore
{
    private async Task<TenantMemoryOperationObservation> RequireMemoryOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.MemoryOperation ?? throw new SecurityConflictException("operation_mismatch");
        var result = await ObserveMemoryOperation(db, run, TenantMemoryToolContract.Parse(value.ToolName, value.ArgumentsJson), value, null, ct).ConfigureAwait(false);
        await TenantMemoryOperationReview.ExecutorAsync(request, result, new IamAuthorizer(this), ct).ConfigureAwait(false); return result;
    }
    private static async Task<bool> OwnMemoryForget(AgentCoreDbContext db, AgentRun run, Guid? memory, string? hash, CancellationToken ct)
    {
        var requests = await (from r in db.Set<TenantOperationReceiptRecord>().AsNoTracking()
            join a in db.Set<TenantApprovalRecord>().AsNoTracking() on r.RequestId equals (Guid?)a.RequestId
            where r.TenantId == run.Owner.TenantId && a.TenantId == r.TenantId && r.SessionId == run.SessionId.ToString("D") && r.RootActivationId == run.Admission.Activation.RootActivationId.ToString("D") && r.Outcome == (int)TenantOperationOutcome.Completed
                && r.ReasonCode == TenantMemoryOperationRules.Completed select a.PayloadJson).ToArrayAsync(ct).ConfigureAwait(false);
        return requests.Select(json => System.Text.Json.JsonSerializer.Deserialize<TenantApprovalRequest>(json)!).Any(a => a.Operation.MemoryOperation is { } forgotten
            && (memory is null ? !TenantMemoryToolContract.ReadOnly(forgotten.ToolName) : forgotten.ToolName == ToolCatalog.MemoryForget && TenantMemoryOperationRules.Sources(forgotten).Any(s => s.MemoryId == memory && s.ContentHash == hash)));
    }
    private async Task StageMemoryEffect(AgentCoreDbContext db, TenantMemoryOperationObservation observed, DateTimeOffset at, CancellationToken ct)
    {
        var values = TenantMemoryOperationMutation.Values(observed, at);
        foreach (var value in values)
        {
            var row = await db.Set<TenantLearnedMemoryRecord>().SingleOrDefaultAsync(i => i.TenantId == value.TenantId && i.MemoryId == value.MemoryId, ct).ConfigureAwait(false);
            if (value.Revision == 1)
            {
                if (row is not null || await db.Set<TenantLearnedMemoryRecord>().AnyAsync(i => i.TenantId == value.TenantId && i.OwnerKey == value.OwnerKey && i.Kind == (int)value.Kind
                    && i.SubjectKey == value.SubjectKey && i.Status == (int)MemoryItemStatus.Active, ct).ConfigureAwait(false)) throw new SecurityConflictException("memory_subject_conflict");
                db.Add(TenantLearnedMemoryRecord.Row(value));
            }
            else
            {
                if (row is null || row.Revision + 1 != value.Revision) throw new SecurityConflictException("memory_source_changed");
                db.Entry(row).CurrentValues.SetValues(TenantLearnedMemoryRecord.Row(value));
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed partial class SqliteIamStore
{
    private async Task<bool> MemoryReadConsented(AgentCoreDbContext db, TenantMemoryReader reader, TenantMemoryReference item, CancellationToken ct)
    {
        var requests = await (from r in db.Set<TenantOperationReceiptRecord>().AsNoTracking()
            join a in db.Set<TenantApprovalRecord>().AsNoTracking() on r.RequestId equals (Guid?)a.RequestId
            join d in db.Set<TenantApprovalDecision>().AsNoTracking() on a.RequestId equals d.RequestId
            where r.TenantId == reader.Run.Owner.TenantId && a.TenantId == r.TenantId && d.TenantId == r.TenantId
                && r.SessionId == reader.Run.SessionId.ToString("D") && r.RootActivationId == reader.Run.Admission.Activation.RootActivationId.ToString("D")
                && r.Outcome == (int)TenantOperationOutcome.Completed && (r.ReasonCode == TenantMemoryOperationRules.Completed || r.ReasonCode == TenantContinuityRules.Completed)
            select new { a.PayloadJson, d.HumanPrincipalId }).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var row in requests)
        {
            var request = System.Text.Json.JsonSerializer.Deserialize<TenantApprovalRequest>(row.PayloadJson)!;
            var value = request.Operation.MemoryOperation ?? request.Operation.ContinuityRead?.Memory;
            if (value is null || !TenantMemoryToolContract.ReadOnly(value.ToolName)
                || !TenantMemoryOperationRules.Sources(value).Any(s => s.MemoryId == item.MemoryId && s.ContentHash == item.ContentHash)) continue;
            if (request.Operation.ContinuityRead is { } continuity && !await ContinuitySourceOwnersAvailable(db, reader, continuity, ct).ConfigureAwait(false)) continue;
            var ids = TenantMemoryOperationRules.Sources(value).Select(s => s.MemoryId).ToArray();
            var references = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == request.TenantId && ids.Contains(i.MemoryId))
                .Select(i => new TenantMemoryReference(i.TenantId, i.MemoryId, (MemoryScope)i.Scope, i.UserPrincipalId, i.AccountId, i.UserId,
                    Guid.Parse(i.AgentInstanceId), i.ExecutorPrincipalId, Guid.Parse(i.SessionId), i.ContentHash, (MemoryItemStatus)i.Status)).ToArrayAsync(ct).ConfigureAwait(false);
            if (references.Length != ids.Length) continue;
            try { await ValidateTenantActorAsync(db, reader.Security, row.HumanPrincipalId, ct).ConfigureAwait(false); await TenantMemoryReadConsent.RequireAsync(request, reader, references, row.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false); return true; }
            catch (AccessDeniedException) { }
        }
        return false;
    }
}
