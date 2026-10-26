using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

// Pure projection shared by the existing administrative owner and the tenant-authorized owner.
// Callers own validation, storage, current authority and filtering of protected child keys.
public static class InstanceVersionPreviewFactory
{
    public static InstanceVersionPreview Create(AgentInstance instance, AgentDefinition baseline, AgentDefinition target,
        IReadOnlyList<AgentDefinitionPublicationResource> oldResources, IReadOnlyList<AgentDefinitionPublicationResource> newResources)
    {
        static bool Different<T>(T left, T right) => JsonSerializer.Serialize(left) != JsonSerializer.Serialize(right);
        var differences = InstanceSettingsResolver.Sections.SelectMany(section =>
            InstanceSettingsResolver.Values(baseline, section).Where(field => Different(field.Value, InstanceSettingsResolver.Values(target, section)[field.Key]))
            .Select(field => section + "." + field.Key))
            .Concat(Enum.GetValues<ExecutionBudgetClass>().Where(kind => baseline.ExecutionBudgets?.Get(kind) != target.ExecutionBudgets?.Get(kind)).Select(kind => "executionBudgets." + kind))
            .Concat(new (string Key, bool Changed)[] { ("identity", Different(baseline.Identity, target.Identity)), ("goals", Different(baseline.Goals, target.Goals)),
                ("executionPolicy", Different(baseline.ExecutionPolicy, target.ExecutionPolicy)), ("metadata", Different(baseline.Metadata, target.Metadata)),
                ("selfManagement", Different(baseline.SelfManagement, target.SelfManagement)) }.Where(x => x.Changed).Select(x => x.Key)).ToArray();
        var preserved = InstanceSettingsResolver.Sections.SelectMany(section => InstanceSettingsResolver.Overrides(instance.SettingsOverrides ?? new(), section).Keys.Select(field => section + "." + field))
            .Concat(Enum.GetValues<ExecutionBudgetClass>().Where(kind => instance.ExecutionBudgets?.Get(kind) is not null).Select(kind => "executionBudgets." + kind))
            .Append("identity").Concat(ManagementOverrides(instance))
            .Concat(instance.UnattendedModel is null ? [] : new[] { "unattendedModel" }).ToArray();
        return new(instance.Revision, baseline.Version, target.Version, differences, preserved,
            target.SkillList.Where(s => !baseline.SkillList.Any(old => old.Id == s.Id)).Select(s => "definition:" + s.Id)
                .Concat(newResources.Where(r => !oldResources.Any(old => old.ResourceId == r.ResourceId)).Select(r => "definition:" + r.ResourceId)).ToArray(),
            baseline.SkillList.Where(s => !target.SkillList.Any(next => next.Id == s.Id)).Select(s => "definition:" + s.Id)
                .Concat(oldResources.Where(r => !newResources.Any(next => next.ResourceId == r.ResourceId)).Select(r => "definition:" + r.ResourceId)).ToArray(),
            "Applies to the next new Run in existing conversations. Current Runs are unchanged.",
            target.SkillList.Where(s => baseline.SkillList.FirstOrDefault(old => old.Id == s.Id) is { } old && Different(old, s)).Select(s => "definition:" + s.Id)
                .Concat(newResources.Where(r => oldResources.FirstOrDefault(old => old.ResourceId == r.ResourceId) is { } old && old != (r with { Version = old.Version })).Select(r => "definition:" + r.ResourceId)).ToArray());
    }

    private static IEnumerable<string> ManagementOverrides(AgentInstance instance)
    {
        if (instance.HarnessManagement is not { } state) yield break;
        if (state.Policy.Frozen) yield return "selfManagement.frozen";
        if (state.Overrides is not { } overrides) { yield return "selfManagement.policy"; yield break; }
        if (overrides.Mode is not null) yield return "selfManagement.mode";
        if (overrides.Scopes is not null) yield return "selfManagement.scopes";
        if (overrides.AutoApply is not null) yield return "selfManagement.autoApply";
        if (overrides.ConfigurationAssistance is not null) yield return "selfManagement.configurationAssistance";
        if (overrides.HarnessAuthoring is not null) yield return "selfManagement.harnessAuthoring";
    }

}
