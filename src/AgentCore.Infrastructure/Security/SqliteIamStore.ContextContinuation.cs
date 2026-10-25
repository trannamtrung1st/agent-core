using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async Task<bool> RequireContextContinuation(AgentCoreDbContext db, AgentRun source, SessionSnapshot snapshot,
        TenantContextContinuationAdmission? admission, CancellationToken ct)
    {
        if (admission is null) return false;
        var state = await ReadTenantAsync(db, source.Owner.TenantId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != admission.ExpectedSecurityRevision) return false;
        await RequireSpaceExecutionSource(db, admission.Run, state, new IamAuthorizer(this), ct, admission: true).ConfigureAwait(false);
        var root = await TenantCoreEventProduction.RootAsync(db, source, ct).ConfigureAwait(false);
        var owner = await OwnedAgents(db, state.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == source.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var runs = (await OwnedRuns(db, state.Tenant.TenantId).AsNoTracking().Where(r => r.SessionId == source.SessionId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain);
        var entries = (await db.Entries.AsNoTracking().Where(e => e.SessionId == source.SessionId.ToString("D")).OrderByDescending(e => e.EntrySequence).Take(100).ToArrayAsync(ct).ConfigureAwait(false)).Select(SqliteMemoryStore.ToEntry).ToArray();
        TenantContextContinuationRules.RequireAdmission(source, root, snapshot, runs, admission, Owned(owner), state.Tenant.Revision, entries);
        return true;
    }
    private async Task StageContextContinuation(AgentCoreDbContext db, SessionSnapshot snapshot, TenantContextContinuationAdmission admission, CancellationToken ct)
    {
        var next = admission.Run;
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, next)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(next));
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == next.Owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(next.ActivationId, tenant.TenantId, next.Owner.ExecutorPrincipalId!.Value, "agent-runs.context.continue", next.AgentRunId.ToString("D"),
            "allowed", "verified_context_continuation", next.CreatedAtUtc));
    }
}
