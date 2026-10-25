using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static class TenantAutomationPresetRules
{
    public static IReadOnlyList<AutomationPreset> Templates { get; } = Array.AsReadOnly(new[] {
        new AutomationPreset("review-recent-work", 1, "Review recent work", "Record useful lessons from substantive user work.",
            "Review the exact owned source Runs represented in this task's trigger references. Inspect each with experience.source before recording useful validated lessons with experience.record. Skip sources this Automation cannot currently read; never adopt private User evidence or invent source IDs or outcomes. Preserve sensitivity and provenance. Do not promote Experience into Memory. If the budget cannot cover every source, identify the unreviewed source IDs in the factual result for a later authorized review. If there is no useful new work, finish quietly with NoAction.",
            "coreEvent", "run.completed", "event.data.activationKind === 'UserTurn' && event.data.outcomeKind !== 'NoAction'", new(EventDispatchMode.CoalesceLatest, 900)),
        new AutomationPreset("consolidate-continuity", 1, "Consolidate continuity", "Consolidate redundant Instance Experience with exact permission and approval.",
            "Inspect a narrow set of this Agent's Instance-owned Experience. Use experience.consolidate only for genuinely repeated observations and only with exact approval. Preserve sensitivity, exceptions, failed attempts, temporal/project qualifiers, provenance and contradictions. Respect Allow agent consolidation. Do not adopt a human's private Profile, Memory or Experience. Never forget or promote Experience into Memory. Make one bounded decision or finish quietly with NoAction.", "schedule"),
        new AutomationPreset("improve-harness", 1, "Improve agent harness", "Propose a governed improvement to the agent harness.",
            "Inspect repeated friction, failures and corrections and the current managed harness. Propose one justified improvement through existing Harness Management, or finish quietly with NoAction. Respect enabled scopes, freeze and immutable Definitions. Request exact approval for guarded changes. This recipe does not authorize automatic publication or adoption, or grant new capabilities.", "schedule") });

    public static AutomationPreset? Template(string? id, int? version)
    {
        if (id is null && version is null) return null;
        return Templates.SingleOrDefault(p => p.PresetId == id && p.PresetVersion == version)
            ?? throw new ArgumentException("Select a current preset ID and version together.");
    }
    public static TriggerProvenance Provenance(Automation? current, TenantAutomationChange change, DateTimeOffset at)
    {
        if (current is not null)
        {
            if ((change.PresetId is not null || change.PresetVersion is not null)
                && (change.PresetId != current.Provenance.PresetId || change.PresetVersion != current.Provenance.PresetVersion))
                throw new SecurityConflictException("automation_provenance_changed");
            return current.Provenance.WithUpdated(at);
        }
        var preset = Template(change.PresetId, change.PresetVersion);
        return new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at, preset?.PresetId, preset?.PresetVersion);
    }
    public static void ValidateNew(Automation? current, Automation proposed)
    {
        if (current is null) _ = Template(proposed.Provenance.PresetId, proposed.Provenance.PresetVersion);
    }
    public static IReadOnlyList<AutomationPresetOption> Options(TenantAgent agent, AgentDefinition definition, TenantIsolationMetadata settings, IModelCatalog models)
    {
        var model = ExecutionModelPolicy.Resolve(models, definition, agent.Instance, null);
        var experience = settings.ExperienceEnabled && definition.StateIsolationPolicy?.CrossSessionContinuity != false
            && definition.StateIsolationPolicy?.ExperienceRecall != false && definition.StateIsolationPolicy?.ExperienceRecord != false;
        var management = ManagementPolicyResolver.Resolve(definition, agent.Instance);
        return Templates.Select(p => {
            var reasons = new List<string>();
            if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) reasons.Add("Restore the Agent before enabling automatic work.");
            if (p.TriggerKind == "coreEvent" ? !OccurrenceCompatibility.Allows(definition, TriggerSourceKind.CoreEvent)
                : !TriggerDurableSchedulingPolicy.AllowsScheduledOccurrence(definition) || definition.TriggerPolicy?.AllowWeekly != true)
                reasons.Add(p.TriggerKind == "coreEvent" ? "Allow Built-in events in the active Definition." : "Allow weekly scheduling in the active Definition.");
            if (!model.Accepted || model.Pin is null || models.Get(model.Pin.OfferingId)?.Tools != true) reasons.Add("Select an available tool-capable unattended model.");
            if (p.PresetId is "review-recent-work" or "consolidate-continuity" && !experience) reasons.Add("Enable eligible Experience for this Agent.");
            if (p.PresetId == "consolidate-continuity")
            {
                if (!settings.AllowMaintenance) reasons.Add("Enable Allow agent consolidation.");
                if (management.Frozen) reasons.Add("Unfreeze the Agent's management policy.");
            }
            if (p.PresetId == "improve-harness" && (management.Frozen || management.Mode == HarnessManagementMode.Disabled || management.Scopes.Count == 0))
                reasons.Add("Enable Harness Management, select a scope, and unfreeze it.");
            return new AutomationPresetOption(p, reasons.Count == 0, reasons.AsReadOnly());
        }).ToArray();
    }
}
