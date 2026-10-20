using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationEditorItem(Automation Automation, bool CanUpdate, bool CanCancel, bool CanRun);
public sealed record TenantAutomationEditor(TenantAgent Agent, TriggerPolicy? Policy, IReadOnlyList<TenantAutomationEditorItem> Items,
    int Total, int Page, int PageSize, long SecurityRevision, bool CanCreate, bool CanSchedule, bool CanCoreEvents = false, bool CanWebhookEvents = false);

public sealed partial class TenantAutomationService
{
    public async ValueTask<TenantAutomationEditor> EditorAsync(Guid tenant, Guid instance, HumanSessionReference human,
        Guid? automation = null, int page = 0, int pageSize = 10, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); await AgentRead(state, instance, human.PrincipalId, ct);
        var (agent, policy) = await configurations.CurrentPolicyAsync(tenant, instance, ct);
        TenantAutomationPage values;
        if (automation is { } id)
        {
            var value = await ReadAsync(tenant, id, human, ct);
            if (value.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
            values = new([value], 1, 0, 1);
        }
        else values = await ListAsync(tenant, instance, human, page, pageSize, ct);
        var active = agent.Instance.Lifecycle == AgentInstanceLifecycle.Active;
        async ValueTask<bool> Can(string permission, AccessScope scope) => active &&
            (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct)).Allowed;
        var items = new List<TenantAutomationEditorItem>();
        foreach (var value in values.Items)
        {
            var scope = TenantAutomationRules.Scope(tenant, value.AutomationId);
            var editable = value.Status is not (AutomationStatus.Cancelled or AutomationStatus.Completed or AutomationStatus.Expired);
            items.Add(new(value, editable && await Can("automations.update", scope), value.Status != AutomationStatus.Cancelled && await Can("automations.delete", scope),
                value.Status == AutomationStatus.Active && !await store.IsTenantAutomationBusyAsync(tenant, value.AutomationId, ct) && await Can("automations.run", scope)));
        }
        var create = await Can("automations.create", AccessScope.ForTenant(tenant));
        await Unchanged(state, human, ct);
        return new(agent, policy.TriggerPolicy, items, values.Total, values.Page, values.PageSize, state.Tenant.Revision, create, AgentCore.Application.Triggers.TriggerDurableSchedulingPolicy.AllowsScheduledOccurrence(policy),
            AgentCore.Application.Triggers.OccurrenceCompatibility.Allows(policy, TriggerSourceKind.CoreEvent),
            AgentCore.Application.Triggers.OccurrenceCompatibility.Allows(policy, TriggerSourceKind.ApplicationEvent));
    }
}
