using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    private async ValueTask RequireSpaceResourceReadSources(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        try
        {
            var sources = TenantSpaceResourceReadRules.Read(run); if (sources.Count == 0) return;
            var tenant = run.Owner.TenantId!.Value;
            var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_source_changed"); var iam = new IamAuthorizer(this);
            await RequireSpaceExecutionSource(db, run, state, iam, ct).ConfigureAwait(false);
            var row = await OwnedAgents(db, tenant).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_source_changed"); var agent = Owned(row);
            var baseline = await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false);
            foreach (var source in sources)
            {
                var request = source.RequestId is null ? null : (await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == source.RequestId, ct).ConfigureAwait(false))?.Domain();
                var receipt = source.OperationId is null ? null : (await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.OperationId == source.OperationId, ct).ConfigureAwait(false))?.Domain();
                var decision = source.RequestId is null ? null : await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenant && r.RequestId == source.RequestId, ct).ConfigureAwait(false);
                var granted = source.RequestId is not null && await db.Set<TenantOperationGrant>().AnyAsync(g => g.TenantId == tenant && g.RequestId == source.RequestId, ct).ConfigureAwait(false);
                await TenantSpaceResourceReadRules.RequireAsync(run, source, state, agent, baseline, iam,
                    (key, revision) => RequireResourcePin(db, key, revision, run, agent, ct), request, receipt, decision, granted, ct).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new AccessDeniedException("resource_source_changed"); }
    }
}
