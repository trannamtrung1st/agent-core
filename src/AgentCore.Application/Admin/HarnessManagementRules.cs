using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record ManagementPolicyView(long InstanceRevision, long PolicyRevision, string Source,
    HarnessManagementPolicy Effective, HarnessManagementPolicy DefinitionDefaults, ManagementPolicyOverrides? Overrides);

/// <summary>Pure policy and freeze preparation; callers own authority and persistence.</summary>
internal static class HarnessManagementRules
{

    internal static ManagementPolicyView View(AgentInstance owner, AgentDefinition definition) =>
        new(owner.Revision, owner.HarnessManagement?.PolicyRevision ?? 1,
            owner.HarnessManagement is null || owner.HarnessManagement.Overrides is { Mode: null, Scopes: null, AutoApply: null, ConfigurationAssistance: null, HarnessAuthoring: null } ? "definition" : "per-field",
            ManagementPolicyResolver.Resolve(definition, owner), definition.SelfManagement ?? HarnessManagementPolicy.Disabled,
            owner.HarnessManagement?.Overrides);

    internal static HarnessManagementState Prepare(AgentInstance owner, AgentDefinition definition, ManagementPolicyOverrides overrides, bool? frozen, bool agent)
    {
        var effective = ManagementPolicyResolver.Resolve(definition, owner);
        if (agent && (!ManagementTools.Enabled(effective) || !effective.Allows(HarnessManagementScope.ManagementPolicy) || frozen == false))
            throw AgentCoreErrors.Forbidden("Only an authorized person can enable or unfreeze self-management.");
        Validate(overrides);
        var old = owner.HarnessManagement ?? new(effective);
        // This catalog is configuration eligibility; IAM and execution policy still authorize each operation.
        var policy = effective with { Frozen = frozen ?? effective.Frozen,
            EligibleTools = effective.EligibleTools.Concat(DefinitionExecutionPolicies.Of(definition).ToolList.Append(ToolCatalog.AttachmentsRead))
                .Where(t => ToolRegistry.TryGet(t, out var descriptor) && descriptor.DefinitionAuthorizable
                    && RolePermissions.AllowsTool(definition, t)).Distinct().ToArray() };
        return old with { Policy = policy, Overrides = overrides, PolicyRevision = checked(old.PolicyRevision + 1) };
    }

    internal static HarnessManagementState PrepareFreeze(AgentInstance owner, AgentDefinition definition)
    {
        var effective = ManagementPolicyResolver.Resolve(definition, owner);
        if (!ManagementTools.Enabled(effective)) throw AgentCoreErrors.Forbidden("Self-management is disabled or frozen.");
        var old = owner.HarnessManagement ?? new(effective);
        return old with { Policy = effective with { Frozen = true }, PolicyRevision = checked(old.PolicyRevision + 1),
            Overrides = old.Overrides ?? new() };
    }

    public static void Validate(ManagementPolicyOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        if (overrides.Mode is { } mode && !Enum.IsDefined(mode.Value)
            || overrides.Scopes is { } scopes && (scopes.Value is null || scopes.Value.Length > 9 || scopes.Value.Distinct().Count() != scopes.Value.Length || scopes.Value.Any(s => !Enum.IsDefined(s)))
            || overrides.AutoApply is { } auto && (auto.Value is null || auto.Value.Length > 3 || auto.Value.Distinct().Count() != auto.Value.Length || auto.Value.Any(s => !Enum.IsDefined(s))))
            throw AgentCoreErrors.Validation("Self-management policy contains an invalid or duplicate operation.");
    }
}
