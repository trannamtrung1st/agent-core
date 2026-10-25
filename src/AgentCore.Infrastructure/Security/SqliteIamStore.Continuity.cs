using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantContinuityStore
{
    public async ValueTask<TenantContinuityObservation> ObserveTenantContinuityAsync(TenantRunReference reference, Guid? generation,
        TenantContinuityArguments arguments, PreparedContinuityRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("continuity_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        return await ObserveContinuity(db, run, arguments, expected, inspectionHuman, ct).ConfigureAwait(false);
    }
    private async Task<TenantContinuityObservation> ObserveContinuity(AgentCoreDbContext db, AgentRun run, TenantContinuityArguments arguments, PreparedContinuityRead? expected, Guid? human, CancellationToken ct)
    {
        var args = TenantContinuityToolContract.Parse(arguments.Tool, arguments.CanonicalArguments); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); var iam = new IamAuthorizer(this);
        if (human is { } actor) await ValidateTenantActorAsync(db, reader.Security, actor, ct).ConfigureAwait(false);
        if (human is not null && expected is null) throw new AccessDeniedException("approval_unavailable");
        var visible = await TenantContinuityRules.VisibleSessionIdsAsync(reader, iam, ct, human).ConfigureAwait(false);
        if (human is null) TenantContinuityRules.Eligible(reader);
        TenantMemoryOperationObservation? memory = null;
        if (human is not null ? expected!.Memory is not null : await TenantContinuityRules.IncludeMemoryAsync(reader, args, iam, ct).ConfigureAwait(false))
            memory = await ObserveMemoryOperation(db, run, TenantMemoryToolContract.Parse(args.Tool, args.CanonicalArguments), expected?.Memory, human, ct).ConfigureAwait(false);
        TenantExperienceFactObservation? experience = null;
        var settings = await ReadIsolationMetadataAsync(db, run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (human is not null ? expected!.Experience is not null : await TenantContinuityRules.IncludeExperienceAsync(reader, settings, args, iam, ct).ConfigureAwait(false))
            experience = await ObserveExperienceFacts(db, run, args, expected?.Experience, human, ct).ConfigureAwait(false);
        var named = human is not null ? TenantContinuityRules.Sessions(expected!) : null;
        var roots = OwnedSessions(db, reader.Security.Tenant.TenantId).AsNoTracking().Where(s => s.AgentInstanceId == run.AgentInstanceId.ToString("D")
            && s.ExecutorPrincipalId == reader.Agent.PrincipalId && s.SessionId != run.SessionId.ToString("D"));
        if (visible is not null) { var ids = visible.Select(id => id.ToString("D")).ToArray(); roots = roots.Where(s => ids.Contains(s.SessionId)); }
        if (named is not null) { var ids = named.Select(s => s.SessionId.ToString("D")).ToArray(); roots = roots.Where(s => ids.Contains(s.SessionId)); }
        else if (args.Kind is "Memory" or "Experience") roots = roots.Where(s => false);
        else if (args.Id is { } sourceId) roots = roots.Where(s => s.SessionId == sourceId.ToString("D"));
        var metadata = await (from root in roots join session in db.Sessions.AsNoTracking() on root.SessionId equals session.SessionId
            join snapshot in db.Snapshots.AsNoTracking() on root.SessionId equals snapshot.SessionId
            where session.RetentionKind == (int)SessionRetentionKind.Saved
            orderby session.UpdatedAtUtc descending, root.SessionId
            select new { root.SessionId, session.Revision, snapshot.LastEntrySequence, session.Title, snapshot.Summary, session.UpdatedAtUtc }).Take(TenantContinuityRules.CandidateLimit + 1).ToArrayAsync(ct).ConfigureAwait(false);
        var documents = new List<TenantHistoricalSession>();
        foreach (var source in named is null ? metadata.Take(TenantContinuityRules.CandidateLimit).ToArray() : named.Select(n => metadata.SingleOrDefault(s => s.SessionId == n.SessionId.ToString("D")) ?? throw new AccessDeniedException("continuity_source_unavailable")).ToArray())
        {
            var id = Guid.Parse(source.SessionId); var witness = named?.Single(s => s.SessionId == id);
            var after = witness?.AfterEntrySequence ?? (args.Kind == "Session" ? args.AfterEntrySequence : Math.Max(0, source.LastEntrySequence - 20));
            var limit = witness?.Limit ?? (args.Kind == "Session" ? args.Limit : 20);
            var rows = await db.Entries.AsNoTracking().Where(e => e.SessionId == source.SessionId && e.EntrySequence > after && e.EntrySequence <= source.LastEntrySequence && e.Status != "Streaming")
                .OrderBy(e => e.EntrySequence).Take(limit + 1).ToArrayAsync(ct).ConfigureAwait(false);
            documents.Add(TenantContinuityRules.Project(id, source.Revision, source.LastEntrySequence, source.Title, source.Summary,
                DateTimeOffset.FromUnixTimeMilliseconds(source.UpdatedAtUtc), rows.Take(limit).Select(SqliteMemoryStore.ToEntry).ToArray(), after, limit, rows.Length > limit));
        }
        return TenantContinuityRules.Observe(reader, args, documents.AsReadOnly(), memory, expected, human is not null,
            human is null && args.Kind is null && metadata.Length > TenantContinuityRules.CandidateLimit, experience);
    }
    public async ValueTask RequireTenantHistoricalSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantHistoricalSource> sources, Guid? generation = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("continuity_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } claim) _ = await RequireRuntimeFrame(db, new(run.Owner, run.SessionId, run.AgentRunId, claim), ct).ConfigureAwait(false);
        else if (!run.IsTerminal) throw new AccessDeniedException("continuity_unavailable");
        await RequireHistoricalSources(db, run, sources, ct).ConfigureAwait(false);
    }
    private async Task RequireHistoricalSources(AgentCoreDbContext db, AgentRun run, IReadOnlyList<TenantHistoricalSource> sources, CancellationToken ct)
    {
        TenantContinuityReadRules.RequireSourcesShape(run, sources); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false); TenantContinuityRules.Eligible(reader);
        foreach (var source in sources)
        {
            var observed = await ObserveContinuity(db, run, TenantContinuityToolContract.Parse(ToolCatalog.ContinuityGet, System.Text.Json.JsonSerializer.Serialize(new {
                kind = "Session", id = source.SessionId, afterEntrySequence = source.AfterEntrySequence, limit = source.Limit })), null, null, ct).ConfigureAwait(false);
            TenantContinuityReadRules.RequireSource(source, observed.Sessions.Single());
            if (!await TenantContinuityReadRules.OrdinaryAsync(observed, new IamAuthorizer(this), ct).ConfigureAwait(false)
                && !await HistoricalReadConsented(db, observed, source, ct).ConfigureAwait(false)) throw new AccessDeniedException("continuity_read_review_required");
        }
    }
}
