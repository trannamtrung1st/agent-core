using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantExperienceStore
{
    private async Task<TenantExperienceSourceObservation> ObserveExperienceSource(AgentCoreDbContext db, AgentRun run, ExperienceSourceKind kind, Guid id,
        TenantExperienceSourceWitness? expected, Guid? human, CancellationToken ct)
    {
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        if (human is null) TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source);
        else await ValidateTenantActorAsync(db, reader.Security, human.Value, ct).ConfigureAwait(false);
        if (TenantExperienceReviewRules.Selected(reader.Root) is { } selected)
        {
            if (kind != selected.Kind || id != selected.SourceId || expected is not null && expected != selected) throw new AccessDeniedException("experience_source_changed");
            expected = selected;
        }
        if (kind == ExperienceSourceKind.Session)
        {
            if (id == run.SessionId || !await OwnedSessions(db, reader.Security.Tenant.TenantId).AnyAsync(s => s.SessionId == id.ToString("D") && s.AgentInstanceId == run.AgentInstanceId.ToString("D")
                && s.ExecutorPrincipalId == reader.Agent.PrincipalId && db.Sessions.Any(saved => saved.SessionId == s.SessionId && saved.DurablyDeletedAtUtc == null), ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_source_unavailable");
            await TenantExperienceRules.RawAsync(reader, new(kind, id, id, 1, 1, 0, 20, null, null, new string('a', 64)), false, iam, ct, human).ConfigureAwait(false);
            var source = await ReadSandboxSession(db, reader.Security.Tenant.TenantId, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_source_unavailable");
            var after = Math.Max(0, source.Snapshot.DurableLastEntrySequence - 100);
            var rows = await db.Entries.AsNoTracking().Where(e => e.SessionId == id.ToString("D") && e.EntrySequence > after && e.EntrySequence <= source.Snapshot.DurableLastEntrySequence)
                .OrderByDescending(e => e.EntrySequence).Take(101).ToArrayAsync(ct).ConfigureAwait(false);
            var entries = rows.Select(SqliteMemoryStore.ToEntry).OrderBy(e => e.Sequence).ToArray();
            return TenantExperienceSourceProjection.Session(reader, settings, source.Snapshot, entries, AgentCore.Application.Experience.ExperienceService.StableSessionCheckpoint(entries), human is null || TenantExperienceReviewRules.Selected(reader.Root) is not null ? expected : null);
        }
        if (kind != ExperienceSourceKind.AgentRun) throw new AccessDeniedException("experience_source_unavailable");
        var metadata = await OwnedRuns(db, reader.Security.Tenant.TenantId).AsNoTracking().Where(r => r.AgentRunId == id.ToString("D") && r.AgentInstanceId == run.AgentInstanceId.ToString("D") && r.ExecutorPrincipalId == reader.Agent.PrincipalId)
            .Select(r => new { r.SessionId, r.Revision }).SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_source_unavailable");
        if (!await db.Sessions.AnyAsync(s => s.SessionId == metadata.SessionId && s.DurablyDeletedAtUtc == null, ct).ConfigureAwait(false)) throw new AccessDeniedException("experience_source_unavailable");
        await TenantExperienceRules.RawAsync(reader, new(kind, id, Guid.Parse(metadata.SessionId), 0, metadata.Revision, 0, 0, id, metadata.Revision, new string('a', 64)), false, iam, ct, human).ConfigureAwait(false);
        var row = await OwnedRuns(db, reader.Security.Tenant.TenantId).AsNoTracking().SingleAsync(r => r.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false); var sourceRun = AgentRunStoreMapping.ToDomain(row);
        var root = await TenantCoreEventProduction.RootAsync(db, sourceRun, ct).ConfigureAwait(false); var privateSource = TenantExperienceRules.HasPrivateEvidence(sourceRun);
        if (privateSource && TenantRunProfileRules.User(sourceRun, root, reader.Security)?.PrincipalId != reader.User?.PrincipalId || human is { } viewer && privateSource && reader.User?.PrincipalId != viewer)
            throw new AccessDeniedException(human is null ? "experience_source_unavailable" : "approval_unavailable");
        await RequireTenantOutputSources(db, sourceRun, ct).ConfigureAwait(false);
        return TenantExperienceSourceProjection.Run(reader, settings, sourceRun, human is null || TenantExperienceReviewRules.Selected(reader.Root) is not null ? expected : null);
    }
    public async ValueTask<TenantExperienceSourceObservation> ObserveTenantExperienceSourceAsync(TenantRunReference reference, Guid? generation,
        ExperienceSourceKind kind, Guid id, TenantExperienceSourceWitness? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveExperienceSource(db, run, kind, id, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantExperienceOperationObservation> ObserveExperienceOperation(AgentCoreDbContext db, AgentRun run, TenantExperienceArguments arguments, PreparedExperienceOperation? expected, Guid? human, CancellationToken ct)
    {
        var args = TenantExperienceToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (human is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        TenantExperienceSourceObservation? source = null; TenantExperience[] items = [];
        if (args.SourceKind is { } kind)
        {
            var witness = expected?.Source ?? (args.Tool == TenantExperienceToolContract.Record ? TenantExperienceReferences.RecordedSource(reader, kind, args.SourceId!.Value, args.ThroughCursor!.Value) : null);
            if (args.Tool == TenantExperienceToolContract.Record && human is null && witness != TenantExperienceReferences.RecordedSource(reader, kind, args.SourceId!.Value, args.ThroughCursor!.Value))
                throw new AccessDeniedException("experience_source_inspection_required");
            source = await ObserveExperienceSource(db, run, kind, args.SourceId!.Value, witness, human, ct).ConfigureAwait(false);
            if (args.Tool == TenantExperienceToolContract.Record && human is null) await RequireExperienceSources(db, run, [new(reader.Security.Tenant.TenantId, source.Witness)], [], ct).ConfigureAwait(false);
        }
        else if (args.SourceIds is { } ids)
        {
            await TenantExperienceRules.RawAsync(reader, null, true, new IamAuthorizer(this), ct, human, privateValue: true).ConfigureAwait(false);
            var keys = new[] { "instance", TenantExperienceRules.OwnerKey(reader) };
            items = (await db.Set<TenantExperienceRecord>().AsNoTracking().Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.AgentInstanceId == run.AgentInstanceId.ToString("D") && keys.Contains(i.OwnerKey) && ids.Contains(i.ExperienceId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(i => i.Domain()).ToArray();
            if (items.Length != ids.Count || items.Any(i => !TenantExperienceRules.Owns(reader, i.Reference()))) throw new AccessDeniedException("experience_sources_unavailable");
        }
        var resultId = args.ReadOnly ? (Guid?)null : TenantExperienceRules.ResultId(reader, args);
        var existing = resultId is null ? null : (await db.Set<TenantExperienceRecord>().AsNoTracking().SingleOrDefaultAsync(i => i.TenantId == reader.Security.Tenant.TenantId && i.ExperienceId == resultId, ct).ConfigureAwait(false))?.Domain();
        var observed = TenantExperienceOperationRules.Observe(reader, settings, args, source, items, existing, expected, human is not null);
        await TenantExperienceRules.RawAsync(reader, source?.Witness, args.Tool == TenantExperienceToolContract.Consolidate, new IamAuthorizer(this), ct, human, privateValue: !args.ReadOnly).ConfigureAwait(false); return observed;
    }
    public async ValueTask<TenantExperienceOperationObservation> ObserveTenantExperienceOperationAsync(TenantRunReference reference, Guid? generation,
        TenantExperienceArguments arguments, PreparedExperienceOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveExperienceOperation(db, run, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantExperienceObservation> ReadTenantRunExperienceAsync(TenantRuntimeFrame frame, string? query = null, Guid? experienceId = null, int limit = 5, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false); var iam = new IamAuthorizer(this); var allowed = false;
        try { TenantExperienceRules.Eligible(reader, settings, TenantExperienceToolContract.Source); await TenantExperienceRules.RawAsync(reader, null, true, iam, ct).ConfigureAwait(false);
            allowed = await TenantExperienceRules.OrdinaryAsync(reader, null, true, false, iam, ct).ConfigureAwait(false); } catch (AccessDeniedException) { }
        var keys = new[] { "instance", TenantExperienceRules.OwnerKey(reader) }; TenantExperience[] items = [];
        if (allowed)
        {
            var rows = (await db.Set<TenantExperienceRecord>().AsNoTracking().Where(i => i.TenantId == reader.Security.Tenant.TenantId && i.AgentInstanceId == run.AgentInstanceId.ToString("D") && keys.Contains(i.OwnerKey)
                && i.Visibility == (int)ExperienceVisibility.Eligible && (experienceId == null || i.ExperienceId == experienceId)).OrderByDescending(i => i.UpdatedAtUtc).ThenBy(i => i.ExperienceId).Take(101).ToArrayAsync(ct).ConfigureAwait(false)).Select(i => i.Domain());
            items = rows.Where(i => TenantExperienceRules.Owns(reader, i.Reference())).Select(i => (Item: i, Score: AgentCore.Application.Continuity.ContinuityService.Score(TenantExperienceRules.SearchText(i), query)))
                .Where(i => string.IsNullOrWhiteSpace(query) || i.Score > 0).OrderByDescending(i => i.Score).ThenByDescending(i => i.Item.UpdatedAt).ThenBy(i => i.Item.ExperienceId).Take(Math.Clamp(limit, 1, 11)).Select(i => i.Item).ToArray();
        }
        return new(reader, settings, items, reader.Security.Tenant.Revision, allowed);
    }
}
