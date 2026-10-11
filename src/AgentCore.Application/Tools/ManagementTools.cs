using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

/// <summary>The additional management admission layer never substitutes for Definition capability authority.</summary>
public static class ManagementTools
{
    public static bool IsManagement(string name) => HarnessChatTools.IsHarness(name) || InstanceSkillTools.IsManagement(name)
        || name.StartsWith("settings.", StringComparison.Ordinal) || name.StartsWith("resources.", StringComparison.Ordinal)
        || name.StartsWith("configuration.", StringComparison.Ordinal) || name.StartsWith("management.", StringComparison.Ordinal)
        || name.StartsWith("credentials.binding.", StringComparison.Ordinal) || name.StartsWith("automation.preferences.", StringComparison.Ordinal);

    public static HarnessManagementScope? Area(string name) => name.Split('.')[0] switch {
        "skills" => HarnessManagementScope.Skills, "settings" => HarnessManagementScope.Settings,
        "resources" => HarnessManagementScope.Resources, "credentials" => HarnessManagementScope.Connections,
        "automation" when name.StartsWith("automation.preferences.", StringComparison.Ordinal) => HarnessManagementScope.AutomationPreferences,
        "management" when name == "management.policy.update" => HarnessManagementScope.ManagementPolicy,
        "configuration" when name == "configuration.propose" => HarnessManagementScope.Settings,
        "harness" when HarnessChatTools.Operations.TryGetValue(name, out var op) => op.Scope,
        _ => null
    };

    public static bool Allows(string name, AgentDefinition definition, HarnessChatContext? context) =>
        context is not null && Enabled(context.Policy) && (Area(name) is not { } area || context.Policy.Allows(area))
        && (definition.SelfManagement is not { } pinned || Enabled(pinned) && (Area(name) is not { } pinArea || pinned.Allows(pinArea)));

    public static bool Enabled(HarnessManagementPolicy policy) => !policy.Frozen && policy.Mode != HarnessManagementMode.Disabled;
}
