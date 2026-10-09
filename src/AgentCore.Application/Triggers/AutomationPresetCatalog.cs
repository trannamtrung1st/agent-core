using AgentCore.Application.Experience;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public sealed record AutomationPreset(string PresetId, int PresetVersion, string Name, string Description, string Instructions,
    string TriggerKind, string? CoreEventKey = null, string? FilterExpression = null, EventDispatch? Dispatch = null);
public sealed record AutomationPresetOption(AutomationPreset Template, bool Eligible, IReadOnlyList<string> Prerequisites);
public sealed class AutomationPresetCatalog(ExperienceService owners, IExperienceStore experiences,
    IAgentDefinitionStore definitions, IModelCatalog models)
{
    public static IReadOnlyList<AutomationPreset> Templates { get; } = Array.AsReadOnly(new[] {
        new AutomationPreset("review-recent-work", 1, "Review recent work", "Record useful lessons from substantive user work.",
            "Review every owned source Run in triggerContext.sources (or triggerContext.source). Inspect each exact Run with experience.source before recording any lesson using experience.record. Deduplicate existing experience. Record only meaningful validated lessons; preserve sensitivity and provenance. Do not promote Experience to Memory. If there is no useful new work, finish quietly with NoAction. Use automation.inspect with this Automation ID to discover pending coverage, continuing with coverageCursor until exhausted. If the budget cannot cover every source, leave the coverage cursor pending and identify the unreviewed source IDs explicitly in the result for a later authorized review.",
            "coreEvent", "run.completed", "event.data.activationKind === 'UserTurn' && event.data.outcomeKind !== 'NoAction'", new(EventDispatchMode.CoalesceLatest, 900)),
        new AutomationPreset("consolidate-continuity", 1, "Consolidate continuity", "Consolidate redundant continuity with current permission and approvals.",
            "Inspect a narrow set of owned Memory or Experience evidence. Use memory.consolidate or experience.consolidate only for legitimate redundancy, preserving scope, kind, sensitivity, qualifiers, provenance and contradictions. Respect Allow agent consolidation and exact approvals. Never forget or automatically promote Experience. Make one safe decision or finish quietly with NoAction.", "schedule"),
        new AutomationPreset("improve-harness", 1, "Improve agent harness", "Propose a governed improvement to the agent harness.",
            "Inspect repeated friction, failures and corrections and the current managed harness. Propose one justified improvement through existing Harness Management, or finish quietly with NoAction. Respect enabled scopes, freeze and immutable Definitions. Request exact approval for guarded changes. This recipe does not authorize automatic publication or adoption, or grant new capabilities.", "schedule") });

    public async ValueTask<IReadOnlyList<AutomationPresetOption>> OptionsAsync(Guid instanceId, CancellationToken ct = default, string? modelKey = null)
    {
        var instance = await owners.RequireInstanceAsync(instanceId, ct);
        var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Definition was not found.");
        var enabled = (await experiences.SettingsAsync(instanceId, ct)).Enabled;
        var maintenance = (await experiences.MaintenanceSettingsAsync(instanceId, ct)).AllowAgentConsolidation;
        return Templates.Select(t => {
            var reasons = new List<string>();
            if (!OccurrenceCompatibility.Allows(definition, t.TriggerKind == "coreEvent" ? TriggerSourceKind.CoreEvent : TriggerSourceKind.Schedule)) reasons.Add("Allow this trigger source in the active Definition.");
            var resolved = ExecutionModelPolicy.Resolve(models, definition, instance, null);
            if (modelKey is not null ? models.Get(modelKey)?.Tools != true : !resolved.Accepted || models.Get(resolved.Pin!.CatalogKey)?.Tools != true) reasons.Add("Select an available tool-capable unattended model.");
            if (t.PresetId == "review-recent-work") { if (!enabled) reasons.Add("Enable Experience for this instance.");  }
            if (t.PresetId == "consolidate-continuity") { if (!maintenance) reasons.Add("Enable Allow agent consolidation."); if (!enabled && definition.MemoryPolicy is not { IdentityUserRetrieval: true }) reasons.Add("Enable eligible owned Memory or Experience continuity."); }
            if (t.PresetId == "improve-harness" && instance.HarnessManagement?.Policy is not { Frozen: false, Mode: not HarnessManagementMode.Disabled, Scopes.Count: > 0 }) reasons.Add("Enable Harness Management, select a scope, and unfreeze it.");
            return new AutomationPresetOption(t, reasons.Count == 0, reasons);
        }).ToArray();
    }
}
