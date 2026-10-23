using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore : ITenantExperienceReviewStore
{
    private async Task<TenantExperienceReviewSource> ExperienceReviewSource(AgentCoreDbContext db, Guid tenant, Guid instance, HumanSessionReference human,
        ExperienceSourceKind kind, Guid id, CancellationToken ct)
    {
        await RequireProfileHumanAsync(db, tenant, human, ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_source_unavailable");
        var agentRow = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(i => i.Instance.InstanceId == instance.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("experience_source_unavailable"); var agent = Owned(agentRow);
        var sourceRun = kind == ExperienceSourceKind.AgentRun ? await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(i => i.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false) : null;
        if (kind == ExperienceSourceKind.AgentRun && (sourceRun is null || sourceRun.AgentInstanceId != instance.ToString("D") || sourceRun.ExecutorPrincipalId != agent.PrincipalId))
            throw new AccessDeniedException("experience_source_unavailable");
        var sessionId = sourceRun is null ? id : Guid.Parse(sourceRun.SessionId);
        var session = await ReadSandboxSession(db, tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("experience_source_unavailable");
        if (session.Owner.AgentInstanceId != instance || session.Owner.ExecutorPrincipalId != agent.PrincipalId) throw new AccessDeniedException("experience_source_unavailable");
        var settings = await ReadIsolationMetadataAsync(db, instance.ToString("D"), ct).ConfigureAwait(false);
        await TenantExperienceReviewRules.RequireAsync(state, agent, human, settings, kind, id, sessionId, new IamAuthorizer(this), ct).ConfigureAwait(false);
        (TenantExperienceSourceWitness Witness, string EvidenceJson, string Title) projected;
        if (kind == ExperienceSourceKind.Session)
        {
            var after = Math.Max(0, session.Snapshot.DurableLastEntrySequence - 100);
            var rows = await db.Entries.AsNoTracking().Where(i => i.SessionId == sessionId.ToString("D") && i.EntrySequence > after && i.EntrySequence <= session.Snapshot.DurableLastEntrySequence)
                .OrderByDescending(i => i.EntrySequence).Take(101).ToArrayAsync(ct).ConfigureAwait(false);
            var entries = rows.Select(SqliteMemoryStore.ToEntry).OrderBy(i => i.Sequence).ToArray();
            projected = TenantExperienceSourceProjection.SessionEvidence(session.Snapshot, entries, AgentCore.Application.Security.TenantExperienceSourceProjection.StableSessionCheckpoint(entries));
        }
        else
        {
            var run = AgentRunStoreMapping.ToDomain(sourceRun!);
            await RequireTenantOutputSources(db, run, ct).ConfigureAwait(false);
            var privateSource = await HasPrivateExperienceEvidence(db, run, ct).ConfigureAwait(false);
            if (privateSource && TenantRunProfileRules.User(run, await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false), state)?.PrincipalId != human.PrincipalId)
                throw new AccessDeniedException("experience_source_unavailable");
            projected = TenantExperienceSourceProjection.RunEvidence(run, privateSource ? human.PrincipalId : null);
        }
        return new(tenant, instance, agent.PrincipalId, human.PrincipalId, projected.Witness, projected.Title, state.Tenant.Revision, agent.Instance.Revision);
    }
    public async ValueTask<TenantExperienceReviewSource> ObserveOwnTenantExperienceReviewAsync(Guid tenant, Guid agent, HumanSessionReference human,
        ExperienceSourceKind kind, Guid source, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        return await ExperienceReviewSource(db, tenant, agent, human, kind, source, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantExperienceReviewReceipt?> TryAdmitOwnTenantExperienceReviewAsync(TenantExperienceReviewAdmission admission,
        HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var selected = admission.Source; var current = await ExperienceReviewSource(db, selected.TenantId, selected.AgentId, human, selected.Witness.Kind, selected.Witness.SourceId, ct).ConfigureAwait(false);
        if (current != selected) return null;
        var agent = Owned(await OwnedAgents(db, selected.TenantId).SingleAsync(i => i.Instance.InstanceId == selected.AgentId.ToString("D"), ct).ConfigureAwait(false));
        var root = TenantExperienceReviewRules.Shape(admission, agent, human, audit); var run = admission.Run;
        var previous = await OwnedRuns(db, selected.TenantId).AsNoTracking().SingleOrDefaultAsync(i => i.AgentRunId == run.AgentRunId.ToString("D"), ct).ConfigureAwait(false);
        if (previous is not null)
        {
            var saved = AgentRunStoreMapping.ToDomain(previous);
            if (saved.Owner != run.Owner || saved.Admission.Activation.DedupeKey != TenantExperienceReviewRules.Key(selected)
                || saved.Admission.TaskIntent?.Text != TenantExperienceReviewRules.Objective(selected)) throw new SecurityConflictException("identity_conflict");
            return new(false, saved.SessionId, saved.AgentRunId, current.SecurityRevision);
        }
        await StageTenantBackgroundGraph(db, root, admission.Session, run, ct).ConfigureAwait(false);
        var row = await db.Set<Tenant>().SingleAsync(i => i.TenantId == selected.TenantId, ct).ConfigureAwait(false);
        db.Entry(row).CurrentValues.SetValues(row with { Revision = checked(row.Revision + 1) }); db.Add(audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(true, run.SessionId, run.AgentRunId, checked(current.SecurityRevision + 1)); }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw new SecurityConflictException("identity_conflict"); }
    }
}
