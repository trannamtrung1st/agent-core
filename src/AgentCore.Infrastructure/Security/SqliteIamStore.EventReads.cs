using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask RequireTenantExecutionSourcesAsync(AgentCoreDbContext db, AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        await RequireSpaceExecutionSource(db, run, state, iam, ct).ConfigureAwait(false);
        await RequireTenantEventRead(db, run, ct).ConfigureAwait(false);
        await TenantExecutionLineage.RequireRequesterAsync(run, state, iam, runs, ct).ConfigureAwait(false);
    }
    private async Task RequireTenantEventRead(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false);
        if (!TenantEventReadRules.IsEventRoot(root) && !TenantAutomationConversationRules.IsConversationRoot(root)) return;
        var tenant = run.Owner.TenantId!.Value;
        var occurrenceRow = await db.TriggerOccurrences.IgnoreQueryFilters().SingleAsync(o => o.TenantId == tenant
            && o.OccurrenceId == root.Admission.Activation.TriggerOccurrenceId!.Value.ToString("D"), ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var targetRoot = TenantAutomationConversationRules.IsConversationRoot(root)
            ? await OwnedSessions(db, tenant).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == root.SessionId.ToString("D"), ct) : null;
        await TenantAutomationConversationRules.RequireExecutionAsync(root, TriggerStoreMapping.ToOccurrence(occurrenceRow),
            targetRoot is null ? null : await SessionView(db, targetRoot, ct), state, new IamAuthorizer(this),
            async (id, token) => (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.RequestId == id, token))?.Domain(),
            async (id, token) => await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == id, token),
            async (id, token) => await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == id, token), ct);
        await TenantEventReadRules.RequireAsync(root, TriggerStoreMapping.ToOccurrence(occurrenceRow), state, new IamAuthorizer(this),
            (kind, id, trigger, token) => new(TenantEvent(db, tenant, kind, id, trigger, token)),
            async (id, token) => { var row = await db.CoreEventBuckets.IgnoreQueryFilters().SingleOrDefaultAsync(b => b.TenantId == tenant && b.BucketId == id.ToString("D"), token).ConfigureAwait(false);
                return row is null ? null : new TenantEventBucket(tenant, EventBucket(row)); },
            async (id, token) => (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.RequestId == id, token).ConfigureAwait(false))?.Domain(),
            async (id, token) => await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == tenant && d.RequestId == id, token).ConfigureAwait(false),
            async (id, token) => await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == tenant && g.RequestId == id, token).ConfigureAwait(false), ct,
            (id, token) => CanDeliverTenantEmail(db, tenant, id, token, retained: true)).ConfigureAwait(false);
    }
}
