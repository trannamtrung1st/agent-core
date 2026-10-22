using AgentCore.Domain.Triggers;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Security;

public sealed partial class TenantAutomationService
{
    public async ValueTask<AgentCore.Domain.Events.EventFilterResult> TestFilterAsync(Guid tenant, Guid instance,
        HumanSessionReference human, Guid? automation, string? expression, System.Text.Json.JsonElement sample, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct).ConfigureAwait(false);
        await AgentRead(state, instance, human.PrincipalId, ct).ConfigureAwait(false);
        if (automation is { } id)
        {
            var saved = await ReadAsync(tenant, id, human, ct).ConfigureAwait(false);
            if (saved.Owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
            await Require(state, human.PrincipalId, "automations.update", TenantAutomationRules.Scope(tenant, id), ct).ConfigureAwait(false);
        }
        else await Require(state, human.PrincipalId, "automations.create", AgentCore.Domain.Security.AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        if (filters is null) throw new AccessDeniedException("event_filter_unavailable");
        var result = sample.ValueKind != System.Text.Json.JsonValueKind.Object || !sample.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != System.Text.Json.JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            ? new AgentCore.Domain.Events.EventFilterResult(null, "error", "filter-envelope-schema") : filters.Evaluate(expression, sample, ct);
        await Unchanged(state, human, ct).ConfigureAwait(false); return result;
    }
}
