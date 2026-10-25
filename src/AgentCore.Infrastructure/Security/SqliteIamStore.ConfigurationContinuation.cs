using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async Task<bool> RequireConfigurationContinuation(AgentCoreDbContext db, AgentRun source, SessionSnapshot snapshot,
        TenantConfigurationContinuationAdmission? admission, CancellationToken ct)
    {
        if (admission is null) return false;
        var state = await ReadTenantAsync(db, source.Owner.TenantId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        if (state.Tenant.Revision != admission.ExpectedSecurityRevision) return false;
        await RequireSpaceExecutionSource(db, admission.Run, state, new IamAuthorizer(this), ct, admission: true).ConfigureAwait(false);
        var root = await TenantCoreEventProduction.RootAsync(db, source, ct).ConfigureAwait(false);
        var owner = await OwnedAgents(db, state.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == source.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var runs = (await OwnedRuns(db, state.Tenant.TenantId).AsNoTracking().Where(r => r.SessionId == source.SessionId.ToString("D")).ToArrayAsync(ct).ConfigureAwait(false)).Select(AgentRunStoreMapping.ToDomain);
        var ids = TenantConfigurationContinuationRules.RequestIds(source).ToArray();
        var requests = (await db.Set<TenantApprovalRecord>().AsNoTracking().Where(r => r.TenantId == state.Tenant.TenantId && ids.Contains(r.RequestId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToDictionary(r => r.RequestId);
        var operations = requests.Values.Select(r => r.Operation.OperationId).ToArray();
        var receipts = (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().Where(r => r.TenantId == state.Tenant.TenantId && operations.Contains(r.OperationId)).ToArrayAsync(ct).ConfigureAwait(false)).Select(r => r.Domain()).ToDictionary(r => r.OperationId);
        var entries = (await db.Entries.AsNoTracking().Where(e => e.SessionId == source.SessionId.ToString("D")).OrderByDescending(e => e.EntrySequence).Take(100).ToArrayAsync(ct).ConfigureAwait(false)).Select(SqliteMemoryStore.ToEntry).ToArray();
        TenantConfigurationContinuationRules.RequireAdmission(source, root, snapshot, runs, admission, Owned(owner), state.Tenant.Revision,
            id => requests.GetValueOrDefault(id), id => receipts.GetValueOrDefault(id), entries);
        return true;
    }
    private async Task StageConfigurationContinuation(AgentCoreDbContext db, SessionSnapshot snapshot, TenantConfigurationContinuationAdmission admission, CancellationToken ct)
    {
        var next = admission.Run;
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(snapshot, next)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(next));
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == next.Owner.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = checked(tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(next.ActivationId, tenant.TenantId, next.Owner.ExecutorPrincipalId!.Value, "agent-runs.configuration.continue", next.AgentRunId.ToString("D"),
            "allowed", "verified_configuration_continuation", next.CreatedAtUtc));
    }
}
