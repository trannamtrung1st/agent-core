using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

// Shared recipes and validation only. Authorization and atomic ownership remain at each caller.
public static class IsolationConfigurationPlanner
{
    public const string AuditSection = "stateIsolationConfiguration";
    public static IReadOnlyList<string> AuditFields => InstanceSettingsResolver.Fields("stateIsolationPolicy").Select(f => "stateIsolationPolicy." + f)
        .Concat(InstanceSettingsResolver.Fields("memoryPolicy").Select(f => "memoryPolicy." + f))
        .Concat(["voice.enabled", "experience.enabled", "identityMaintenance.allowAgentConsolidation"]).ToArray();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Sections = ["stateIsolationPolicy", "memoryPolicy", "voice"];
    public static IsolationDraft Preset(IsolationConfigurationView view, string preset)
    {
        if (preset is not ("Persistent" or "SessionIsolated" or "Stateless" or "DefinitionDefaults")) throw AgentCoreErrors.Validation("Choose a supported preset draft.");
        var state = view.Settings.Single(s => s.Section == "stateIsolationPolicy");
        var memory = view.Settings.Single(s => s.Section == "memoryPolicy");
        var patches = new List<IsolationSectionPatch>();
        var persistent = preset == "Persistent";
        if (preset == "DefinitionDefaults")
        {
            patches.Add(new("stateIsolationPolicy", new Dictionary<string, JsonElement>(), state.Overrides.Keys.ToArray()));
            patches.Add(new("memoryPolicy", new Dictionary<string, JsonElement>(), memory.Overrides.Keys.ToArray()));
            var voice = view.Settings.Single(s => s.Section == "voice");
            patches.Add(new("voice", new Dictionary<string, JsonElement>(), voice.Overrides.ContainsKey("enabled") ? ["enabled"] : []));
        }
        else if (persistent)
        {
            patches.Add(new("stateIsolationPolicy", JsonSerializer.SerializeToElement(StateIsolationPolicy.Default with { AgentStateVisibility = AgentStateVisibility.InstanceSummary }, Json)
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value), []));
            patches.Add(new("memoryPolicy", new Dictionary<string, JsonElement>(), memory.Overrides.Keys.ToArray()));
        }
        else
        {
            var values = new Dictionary<string, object?> {
                ["conversationContextScope"] = preset == "Stateless" ? "CurrentRequest" : "Session",
                ["homeAccess"] = "Disabled", ["workingCleanup"] = "OnSessionDeletion", ["browserIsolation"] = "EphemeralSession",
                ["agentStateVisibility"] = (state.Constraints["agentStateVisibility"].Choices ?? []).Contains("CurrentSession") ? "CurrentSession" : "Off",
                ["experienceRecord"] = false, ["experienceRecall"] = false, ["profileEvidence"] = false, ["crossSessionContinuity"] = false
            };
            patches.Add(new("stateIsolationPolicy", values.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value, Json)), []));
            var learned = new Dictionary<string, JsonElement>();
            foreach (var field in InstanceSettingsResolver.Fields("memoryPolicy"))
                learned[field] = JsonSerializer.SerializeToElement(field == "sessionMemory" && preset != "Stateless" && memory.Effective[field] is true);
            patches.Add(new("memoryPolicy", learned, []));
            if (preset == "Stateless") patches.Add(new("voice", new Dictionary<string, JsonElement> { ["enabled"] = JsonSerializer.SerializeToElement(false) }, []));
        }
        var preserveOptIns = persistent || preset == "DefinitionDefaults";
        return new(patches, preserveOptIns && view.ExperienceEnabled, preserveOptIns && view.AllowMaintenance);
    }

    public static async ValueTask<IsolationChangePlan> PreviewAsync(Guid id, IsolationConfigurationView view,
        AgentInstance owner, AgentDefinition baseline, IsolationDraft draft, IBrowser browser, TimeProvider time,
        CancellationToken ct = default, bool observeBrowser = true)
    {
        var blocked = new List<string>();
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) blocked.Add("Archived Instance configuration is read-only.");
        var current = owner.SettingsOverrides ?? new();
        var next = Patch(current, draft);
        AgentDefinition? effective = null;
        try { effective = InstanceSettingsResolver.Resolve(baseline, next); }
        catch (AgentCoreException error) { blocked.Add(error.Message); }
        if (effective?.StateIsolationPolicy?.BrowserIsolation == BrowserIsolation.PersistentAgent && browser.HostPolicy.ProfileMode != BrowserProfileMode.PersistentAgent)
            blocked.Add("This host does not permit persistent browser profiles. Choose Session isolation or the host default.");
        if (effective is not null && RequestIsolation.Required(effective) && effective.Voice.Enabled)
            blocked.Add("Request-isolated Sessions currently support text input. Disable Voice in the same draft.");
        var changes = new List<IsolationChange>();
        if (effective is not null)
        {
            var before = InstanceSettingsResolver.Resolve(baseline, current);
            foreach (var patch in draft.Settings)
                foreach (var field in patch.Set.Keys.Concat(patch.Clear).Order(StringComparer.Ordinal))
                {
                    var oldValue = JsonSerializer.SerializeToElement(InstanceSettingsResolver.Values(before, patch.Section)[field], Json);
                    var newValue = JsonSerializer.SerializeToElement(InstanceSettingsResolver.Values(effective, patch.Section)[field], Json);
                    var source = InstanceSettingsResolver.Overrides(next, patch.Section).ContainsKey(field) ? "instance" : "definition";
                    var oldSource = InstanceSettingsResolver.Overrides(current, patch.Section).ContainsKey(field) ? "instance" : "definition";
                    if (oldValue.GetRawText() != newValue.GetRawText() || source != oldSource)
                        changes.Add(new(patch.Section, field, oldValue, newValue, source));
                }
        }
        if (view.ExperienceEnabled != draft.ExperienceEnabled)
            changes.Add(new("experience", "enabled", JsonSerializer.SerializeToElement(view.ExperienceEnabled), JsonSerializer.SerializeToElement(draft.ExperienceEnabled), "instance"));
        if (view.AllowMaintenance != draft.AllowMaintenance)
            changes.Add(new("identityMaintenance", "allowAgentConsolidation", JsonSerializer.SerializeToElement(view.AllowMaintenance), JsonSerializer.SerializeToElement(draft.AllowMaintenance), "instance"));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { id, view.Revisions, next,
            draft.ExperienceEnabled, draft.AllowMaintenance, view.HostBrowserMode }, Json)));
        var adoption = "";
        if (observeBrowser && effective is not null && (effective.StateIsolationPolicy?.BrowserIsolation ?? BrowserIsolation.HostDefault)
            != (InstanceSettingsResolver.Resolve(baseline, current).StateIsolationPolicy?.BrowserIsolation ?? BrowserIsolation.HostDefault))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                var observation = browser is IBrowserStateReader reader
                    ? await reader.ObserveAsync(id, budget.Token).AsTask().WaitAsync(budget.Token) : null;
                adoption = observation?.Availability == "available"
                    ? observation.LiveContexts > 0 ? " Existing live browser contexts transition per Session at the next safe authorized use; profile bytes remain."
                        : " No live browser context is observed; a future authorized use resolves the saved policy."
                    : observation?.Availability == "partial" ? " Browser work is busy or uncertain. Metadata can save, but native transitions remain pending until safe."
                    : " Browser state is unavailable. Saving allocates no browser; future authorized use must satisfy the saved policy.";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { adoption = " Browser transition status is unknown. Metadata can save; unsafe native reuse remains blocked."; }
        }
        var retention = (effective is null ? (WorkingCleanup?)null : (effective.StateIsolationPolicy ?? StateIsolationPolicy.Default).WorkingCleanup) switch
        {
            WorkingCleanup.OnSessionDeletion => " Working files remain after a request completes until the Session is deleted. Choose After safe terminal cleanup to remove scratch after terminal completion.",
            WorkingCleanup.AfterTerminal => " Working files are eligible for safe cleanup after terminal completion; existing history and Artifacts remain.",
            _ => ""
        };
        return new(view.Revisions, draft, fingerprint, changes, blocked,
            "New Runs use saved configuration. Tightened access gates new effects immediately; live sandbox generations are retired before tightened mount access is shown as enforced; uncertain browser or sandbox transitions stay pending. Request isolation requires New chat for each request. Existing profiles, home files, history and Artifacts are retained." + retention + adoption);
    }

    public static InstanceSettingsOverrides Patch(InstanceSettingsOverrides current, IsolationDraft draft)
    {
        if (draft.Settings is null || draft.Settings.Count > 3 || draft.Settings.Select(p => p.Section).Distinct(StringComparer.Ordinal).Count() != draft.Settings.Count)
            throw AgentCoreErrors.Validation("Use at most one patch per State, Memory and Voice section.");
        foreach (var patch in draft.Settings)
        {
            if (patch is null || patch.Set is null || patch.Clear is null || patch.Set.Count + patch.Clear.Count > 32)
                throw AgentCoreErrors.Validation("Each coordinated section needs bounded set and clear fields.");
            if (!Sections.Contains(patch.Section) || patch.Section == "voice" && patch.Set.Keys.Concat(patch.Clear).Any(f => f != "enabled"))
                throw AgentCoreErrors.Validation("Only State & Isolation, Memory and Voice enablement are supported by this coordinated operation.");
            current = InstanceSettingsResolver.Patch(current, patch.Section, patch.Set, patch.Clear);
        }
        return current;
    }
}
