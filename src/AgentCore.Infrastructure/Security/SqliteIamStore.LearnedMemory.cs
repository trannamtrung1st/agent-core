using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantLearnedMemoryStore
{
    private async Task<TenantMemoryReader> MemoryReader(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("memory_context_unavailable");
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("memory_context_unavailable");
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false); var user = TenantRunProfileRules.User(run, root, state);
        if (root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage && (user is null
            || !await db.Set<Account>().AnyAsync(a => a.AccountId == user.AccountId && a.Status == IdentityStatus.Active, ct).ConfigureAwait(false)))
            throw new AccessDeniedException("memory_context_unavailable");
        var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("memory_context_unavailable");
        var parent = await ReadSandboxSession(db, tenant, root.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("memory_context_unavailable");
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("memory_context_unavailable"); var agent = Owned(row);
        if (run.Admission.Configuration is null) throw new AccessDeniedException("memory_context_unavailable");
        await TenantExecutionLineage.RequireRequesterAsync(run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false);
        return new(run, root, state, agent, InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides), session, parent, user);
    }
    private static async Task<IReadOnlyList<TenantLearnedMemory>> OwnedMemory(AgentCoreDbContext db, TenantMemoryReader reader, IReadOnlyList<MemoryProposal>? proposals, CancellationToken ct)
    {
        var keys = TenantMemoryRules.OwnerKeys(reader).ToArray(); var tenant = reader.Security.Tenant.TenantId;
        var query = db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == tenant && keys.Contains(i.OwnerKey));
        var rows = (await query.Where(i => i.Status == (int)MemoryItemStatus.Active).OrderBy(i => i.MemoryId)
            .Take(MemoryLimits.MaxActiveItems * 3 + 1).ToArrayAsync(ct).ConfigureAwait(false)).Select(i => i.Domain()).ToList();
        if (rows.Count > MemoryLimits.MaxActiveItems * 3) throw new AccessDeniedException("memory_capacity_invalid");
        foreach (var proposal in proposals?.Where(p => p.Operation == MemoryProposalOperation.Resolve) ?? [])
        foreach (var key in keys)
        {
            var subject = StructuredMemoryItem.SubjectKeyFor(StructuredMemoryItem.CollapseSubject(proposal.Subject));
            var resolved = await query.Where(i => i.OwnerKey == key && i.Status == (int)MemoryItemStatus.Resolved && i.Kind == (int)proposal.Kind && i.SubjectKey == subject)
                .OrderByDescending(i => i.UpdatedAtUtc).ThenBy(i => i.MemoryId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (resolved is not null && rows.All(i => i.MemoryId != resolved.MemoryId)) rows.Add(resolved.Domain());
        }
        return rows.Where(i => TenantMemoryRules.Owns(reader, i)).OrderByDescending(i => i.UpdatedAt).ThenBy(i => i.MemoryId).ToArray();
    }
    private ValueTask<bool> MemoryAllowed(TenantMemoryReader reader, TenantLearnedMemory? source, bool write, CancellationToken ct) => source is null
        ? TenantMemoryRules.AvailableAsync(reader, write, new IamAuthorizer(this), ct) : TenantMemoryRules.SourceAllowedAsync(reader, source, write, new IamAuthorizer(this), ct);
    public async ValueTask<TenantMemoryObservation> ReadTenantRunMemoryAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        var allowed = new List<TenantLearnedMemory>();
        var readable = reader.Effective.MemoryPolicy?.SessionMemory == true && await MemoryAllowed(reader, null, false, ct).ConfigureAwait(false);
        if (readable)
            foreach (var item in await OwnedMemory(db, reader, null, ct).ConfigureAwait(false))
                if (TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, item.Scope, false)
                    && await MemoryAllowed(reader, item, false, ct).ConfigureAwait(false)) allowed.Add(item);
        var items = TenantMemoryRules.Project(allowed);
        return new(items, Array.AsReadOnly(items.Select(i => new TenantMemorySource(i.TenantId, i.MemoryId, i.ContentHash)).ToArray()), reader.Security.Tenant.Revision, reader.Agent.Instance.Revision, reader.User?.PrincipalId, readable);
    }
    public async ValueTask RequireTenantMemoryProjectionAsync(TenantRuntimeFrame frame, TenantMemoryObservation observation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        if (observation.SecurityRevision != reader.Security.Tenant.Revision || observation.InstanceRevision != reader.Agent.Instance.Revision
            || observation.UserPrincipalId != reader.User?.PrincipalId || !observation.ReadAllowed || reader.Effective.MemoryPolicy?.SessionMemory != true || !await MemoryAllowed(reader, null, false, ct).ConfigureAwait(false)
            || observation.Items.Count != observation.Sources.Count || observation.Items.Count > MemoryLimits.PromptMaxItems) throw new AccessDeniedException("memory_context_changed");
        var ids = observation.Items.Select(i => i.MemoryId).ToArray();
        var rows = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == run.Owner.TenantId && ids.Contains(i.MemoryId)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var item in observation.Items)
            if (rows.SingleOrDefault(i => i.MemoryId == item.MemoryId)?.Domain() != item || item.Status != MemoryItemStatus.Active
                || !TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, item.Scope, false)
                || !observation.Sources.Contains(new(item.TenantId, item.MemoryId, item.ContentHash))) throw new AccessDeniedException("memory_context_changed");
        await RequireMemorySources(db, run, observation.Sources, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireTenantMemorySourcesAsync(TenantRunReference reference, IReadOnlyList<TenantMemorySource> sources, Guid? generation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("memory_context_unavailable"); var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } expected && (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != expected
            || run.Claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()) || generation is null && !run.IsTerminal)
            throw new AccessDeniedException("memory_context_unavailable");
        await RequireMemorySources(db, run, sources, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireMemorySources(AgentCoreDbContext db, AgentRun run, IReadOnlyList<TenantMemorySource> sources, CancellationToken ct)
    {
        if (sources.Count > MemoryLimits.MaxActiveItems || sources.Distinct().Count() != sources.Count) throw new AccessDeniedException("memory_source_unavailable");
        if (sources.Count == 0) return; var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        var ordinaryRead = await MemoryAllowed(reader, null, false, ct).ConfigureAwait(false);
        var ids = sources.Select(s => s.MemoryId).ToArray();
        var rows = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == run.Owner.TenantId && ids.Contains(i.MemoryId))
            .Select(i => new TenantMemoryReference(i.TenantId, i.MemoryId, (MemoryScope)i.Scope, i.UserPrincipalId, i.AccountId, i.UserId,
                Guid.Parse(i.AgentInstanceId), i.ExecutorPrincipalId, Guid.Parse(i.SessionId), i.ContentHash, (MemoryItemStatus)i.Status)).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var source in sources)
        {
            var item = rows.SingleOrDefault(i => i.MemoryId == source.MemoryId);
            if (source.TenantId != run.Owner.TenantId || item is null || item.ContentHash != source.ContentHash
                || !TenantMemoryRules.ScopeEnabled(run.Admission.Configuration!.Definition.MemoryPolicy, item.Scope, false)
                || item.Status == MemoryItemStatus.Deleted && !source.MetadataOnly && !await OwnMemoryForget(db, run, source.MemoryId, source.ContentHash, ct).ConfigureAwait(false)
                || !(ordinaryRead && await TenantMemoryRules.SourceAllowedAsync(reader, item, false, new IamAuthorizer(this), ct, source.MetadataOnly || await OwnMemoryForget(db, run, source.MemoryId, source.ContentHash, ct).ConfigureAwait(false)).ConfigureAwait(false)
                    || await MemoryReadConsented(db, reader, item, ct).ConfigureAwait(false))) throw new AccessDeniedException("memory_source_changed");
        }
    }
    public async ValueTask<TenantMemoryPlan> PrepareTenantLearningAsync(TenantRuntimeFrame frame, SessionSnapshot proposed, IReadOnlyList<MemoryProposal> proposals,
        IReadOnlyList<Guid> candidateIds, Guid auditId, DateTimeOffset preparedAt, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        return await PrepareLearning(db, frame, proposed, proposals, candidateIds, auditId, preparedAt, ct).ConfigureAwait(false);
    }
    private async Task<TenantMemoryPlan> PrepareLearning(AgentCoreDbContext db, TenantRuntimeFrame frame, SessionSnapshot proposed, IReadOnlyList<MemoryProposal> proposals,
        IReadOnlyList<Guid> candidateIds, Guid auditId, DateTimeOffset preparedAt, CancellationToken ct)
    {
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false); var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        TenantRuntimeRules.ValidateSnapshot(frame, reader.Session.Snapshot, proposed);
        var candidates = candidateIds.ToArray();
        if (preparedAt > (clock ?? TimeProvider.System).GetUtcNow() || await db.Set<TenantLearnedMemoryRecord>().AnyAsync(i => i.TenantId == run.Owner.TenantId && candidates.Contains(i.MemoryId), ct).ConfigureAwait(false)
            || await db.Set<AuthorizationAudit>().AnyAsync(a => a.AuditId == auditId, ct).ConfigureAwait(false)) throw new AccessDeniedException("memory_learning_changed");
        var owned = await OwnedMemory(db, reader, proposals, ct).ConfigureAwait(false); var writable = new HashSet<Guid>();
        foreach (var item in owned) if (await MemoryAllowed(reader, item, true, ct).ConfigureAwait(false)) writable.Add(item.MemoryId);
        return TenantMemoryRules.Plan(frame, reader, owned, writable, proposals, candidateIds, auditId, preparedAt,
            await MemoryAllowed(reader, null, true, ct).ConfigureAwait(false) && !await OwnMemoryForget(db, reader.Run, null, null, ct).ConfigureAwait(false), proposed.Entries);
    }
    private async Task StageLearningCommit(AgentCoreDbContext db, TenantRuntimeFrame frame, SessionSnapshot snapshot, TenantMemoryPlan? plan, CancellationToken ct)
    {
        if (plan is null) return;
        if (plan.Frame != frame) throw new AccessDeniedException("memory_learning_changed");
        var actual = await PrepareLearning(db, frame, snapshot, plan.Proposals, plan.CandidateIds, plan.AuditId, plan.PreparedAt, ct).ConfigureAwait(false);
        TenantMemoryRules.Match(actual, plan, snapshot);
        if (plan.Changes.Count == 0) return;
        var cache = new Dictionary<Guid, TenantLearnedMemoryRecord>();
        foreach (var change in plan.Changes)
        {
            if (!cache.TryGetValue(change.Value.MemoryId, out var old))
                old = await db.Set<TenantLearnedMemoryRecord>().SingleOrDefaultAsync(i => i.TenantId == change.Value.TenantId && i.MemoryId == change.Value.MemoryId, ct).ConfigureAwait(false);
            if ((old?.Revision ?? 0) != change.ExpectedRevision) throw new AccessDeniedException("memory_learning_changed");
            var value = TenantLearnedMemoryRecord.Row(change.Value);
            if (old is null) { db.Add(value); cache[value.MemoryId] = value; }
            else { db.Entry(old).CurrentValues.SetValues(value); cache[value.MemoryId] = old; }
            // Release an old active subject before its replacement, including multiple
            // same-subject intents in this batch. All phases remain in the outcome transaction.
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == frame.Owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(plan.AuditId, tenant.TenantId, frame.Owner.ExecutorPrincipalId!.Value, "memory.learn", frame.RunId.ToString("D"),
            "allowed", "owned_response_memory_committed", plan.PreparedAt));
    }
    private static async Task RequireLearnedMemoryReferences(AgentCoreDbContext db, TenantSecurityState next, CancellationToken ct)
    {
        var owners = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == next.Tenant.TenantId && i.UserPrincipalId != null)
            .Select(i => new { i.UserId, i.UserPrincipalId, i.AccountId }).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
        if (owners.Any(i => !next.Users.Any(u => u.UserId == i.UserId && u.PrincipalId == i.UserPrincipalId && u.AccountId == i.AccountId)))
            throw new ArgumentException("Retain the actual learned-memory User owner; change its status without reparenting it.");
    }
    private async Task RequireStoredMemorySources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        await RequireExperienceSources(db, run, TenantExperienceReferences.Sources(run.Checkpoint), TenantExperienceReferences.Facts(run.Checkpoint), ct).ConfigureAwait(false);
        if (TenantHistoricalReferences.Read(run.Checkpoint) is { Count: > 0 } history) await RequireHistoricalSources(db, run, history, ct).ConfigureAwait(false);
        if (TenantMemoryOperationReferences.Read(run.Checkpoint) is { Count: > 0 } sources)
            await RequireMemorySources(db, run, sources, ct).ConfigureAwait(false);
    }
    private async Task RequireMemoryCheckpoint(AgentCoreDbContext db, AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        await RequireExperienceCheckpoint(db, run, checkpoint, ct).ConfigureAwait(false);
        var oldHistory = TenantHistoricalReferences.Read(run.Checkpoint); var nextHistory = TenantHistoricalReferences.Read(checkpoint);
        if (oldHistory.Except(nextHistory).Any()) throw new AccessDeniedException("continuity_source_unavailable");
        if (nextHistory.Count > 0) await RequireHistoricalSources(db, run, nextHistory, ct).ConfigureAwait(false);
        var old = TenantMemoryOperationReferences.Read(run.Checkpoint);
        var proposed = TenantMemoryOperationReferences.Read(checkpoint);
        if (old.Except(proposed).Any()) throw new AccessDeniedException("memory_source_unavailable");
        await RequireMemorySources(db, run, proposed, ct).ConfigureAwait(false);
        var added = proposed.Except(old).Select(s => s.MemoryId).ToArray(); if (added.Length == 0) return;
        var reader = await MemoryReader(db, run, ct).ConfigureAwait(false);
        var rows = await db.Set<TenantLearnedMemoryRecord>().AsNoTracking().Where(i => i.TenantId == run.Owner.TenantId && added.Contains(i.MemoryId)).ToArrayAsync(ct).ConfigureAwait(false);
        if (rows.Any(i => !TenantMemoryRules.ScopeEnabled(reader.Effective.MemoryPolicy, (MemoryScope)i.Scope, false))) throw new AccessDeniedException("memory_context_changed");
    }
}
