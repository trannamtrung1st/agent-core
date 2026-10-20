using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private ValueTask RequireTenantExecutionSourcesAsync(AgentRun run, TenantSecurityState state, IamAuthorizer iam, ITenantRunStore runs, CancellationToken ct)
    {
        RequireTenantEventRead(run, ct);
        return TenantExecutionLineage.RequireRequesterAsync(run, state, iam, runs, ct);
    }
    private void RequireTenantEventRead(AgentRun run, CancellationToken ct)
    {
        lock (automationBacking.AdmissionGate) lock (webhookBacking.TenantGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            var root = TenantCoreRoot(run);
            if (!TenantEventReadRules.IsEventRoot(root)) return;
            var tenant = run.Owner.TenantId!.Value;
            var occurrence = automationBacking.ReadTenantOccurrence(tenant, root.Admission.Activation.TriggerOccurrenceId!.Value)
                ?? throw new AccessDeniedException("event_source_unavailable");
            TenantEventReadRules.RequireAsync(root, occurrence, _tenants[tenant], new IamAuthorizer(this),
                (kind, id, trigger, _) => ValueTask.FromResult(TenantEvent(tenant, kind, id, trigger)),
                (id, _) => ValueTask.FromResult(sessionBacking.CoreEvents.TenantBucket(tenant, id) is { } b ? new TenantEventBucket(tenant, b) : null),
                (id, _) => ValueTask.FromResult(_approvals.GetValueOrDefault(id)),
                (id, _) => ValueTask.FromResult(_approvalDecisions.GetValueOrDefault(id)),
                (id, _) => ValueTask.FromResult(_operationGrants.GetValueOrDefault(id)), ct,
                (id, _) => ValueTask.FromResult(CanDeliverTenantEmail(tenant, id, retained: true))).AsTask().GetAwaiter().GetResult();
        }
    }
}
