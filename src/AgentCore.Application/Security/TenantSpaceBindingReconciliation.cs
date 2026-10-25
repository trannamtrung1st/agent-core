using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;

namespace AgentCore.Application.Security;

// Stable exclusions disappear with their baseline source. Re-enabling/rebinding
// later must not revive an old, invisible contextual restriction.
public static class TenantSpaceBindingReconciliation
{
    public static IReadOnlySet<string> RemovedResourceKeys(TenantAgentConfigurationChange change,
        IReadOnlySet<AccessScope>? before = null, IReadOnlySet<AccessScope>? after = null)
    {
        var removed = new HashSet<string>(StringComparer.Ordinal);
        if (change.ActiveVersion is not null)
        {
            if (before is null || after is null) throw new ArgumentException("Version adoption requires both owned child catalogs.");
            foreach (var scope in before.Except(after).Where(s => s.ResourceType == "resource"))
                if (TenantAgentChildScopes.TryParse(scope, out var instance, out var key) && instance == change.InstanceId)
                    removed.Add((key.StartsWith("definition:", StringComparison.Ordinal) ? nameof(SpaceBindingKind.DefinitionResource) : nameof(SpaceBindingKind.InstanceResource)) + ":" + key);
        }
        if (change.Resource?.DeleteResourceId is { } deleted) removed.Add($"{SpaceBindingKind.InstanceResource}:instance:{deleted:D}");
        if (change.Resource?.Resource is { Enabled: false } disabled) removed.Add($"{SpaceBindingKind.InstanceResource}:instance:{disabled.ResourceId:D}");
        if (change.Resource?.DefinitionState is { EnabledOverride: false } inherited) removed.Add($"{SpaceBindingKind.DefinitionResource}:definition:{inherited.ResourceId:D}");
        return removed;
    }
}
