using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record IsolationSectionPatch(string Section, IReadOnlyDictionary<string, JsonElement> Set, IReadOnlyList<string> Clear);
public sealed record IsolationDraft(IReadOnlyList<IsolationSectionPatch> Settings, bool ExperienceEnabled, bool AllowMaintenance);
public sealed record IsolationChange(string Owner, string Field, JsonElement Before, JsonElement After, string Source);
public sealed record IsolationConfigurationView(IsolationOwnerRevisions Revisions, IReadOnlyList<InstanceSettingsSection> Settings,
    bool ExperienceEnabled, bool AllowMaintenance, string HostBrowserMode, bool BrowserEnabled);
public sealed record IsolationChangePlan(IsolationOwnerRevisions Expected, IsolationDraft Draft, string Fingerprint,
    IReadOnlyList<IsolationChange> Changes, IReadOnlyList<string> BlockingReasons, string Notice);

public sealed class StateIsolationConfigurationService(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IExperienceStore experience, AgentInstanceSettingsService settings, IStateIsolationConfigurationStore store,
    IBrowser browser, IIdGenerator ids, TimeProvider time)
{
    public const string AuditSection = "stateIsolationConfiguration";
    public static IReadOnlyList<string> AuditFields => InstanceSettingsResolver.Fields("stateIsolationPolicy").Select(f => "stateIsolationPolicy." + f)
        .Concat(InstanceSettingsResolver.Fields("memoryPolicy").Select(f => "memoryPolicy." + f))
        .Concat(["voice.enabled", "experience.enabled", "identityMaintenance.allowAgentConsolidation"]).ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Sections = ["stateIsolationPolicy", "memoryPolicy", "voice"];

    public async ValueTask<IsolationConfigurationView> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var all = await settings.ReadAllAsync(id, ct);
        var learned = await experience.SettingsAsync(id, ct);
        var maintenance = await experience.MaintenanceSettingsAsync(id, ct);
        var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (all.Any(s => s.InstanceRevision != owner.Revision)) throw AgentCoreErrors.Conflict("Instance changed while reading State & Isolation. Retry.");
        return new(new(owner.Revision, learned.Revision, maintenance.Revision), all.Where(s => Sections.Contains(s.Section)).ToArray(),
            learned.Enabled, maintenance.AllowAgentConsolidation, browser.HostPolicy.ProfileMode.ToString(), browser.HostPolicy.Enabled);
    }

    public async ValueTask<IsolationChangePlan> PresetAsync(Guid id, string preset, CancellationToken ct = default)
    {
        if (preset is not ("Persistent" or "SessionIsolated" or "Stateless" or "DefinitionDefaults")) throw AgentCoreErrors.Validation("Choose a supported preset draft.");
        var view = await ReadAsync(id, ct);
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
            patches.Add(new("stateIsolationPolicy", JsonSerializer.SerializeToElement(StateIsolationPolicy.Default, Json)
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
        var plan = await PreviewAsync(id, new(patches, preserveOptIns && view.ExperienceEnabled, preserveOptIns && view.AllowMaintenance), ct);
        if (plan.Expected != view.Revisions)
            throw AgentCoreErrors.Conflict("State & Isolation changed while preparing the preset. Retry; your draft is retained.");
        return plan;
    }

    public async ValueTask<IsolationChangePlan> PreviewAsync(Guid id, IsolationDraft draft, CancellationToken ct = default)
    {
        var view = await ReadAsync(id, ct);
        var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (owner.Revision != view.Revisions.Instance) throw AgentCoreErrors.Conflict("Instance changed while preparing preview.");
        var baseline = await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.Persistence("Definition is unavailable.");
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
        if (effective is not null && (effective.StateIsolationPolicy?.BrowserIsolation ?? BrowserIsolation.HostDefault)
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
            "New Runs use saved configuration. Tightened access gates new effects immediately; busy or uncertain browser transitions stay pending. Request isolation requires New chat for each request. Existing profiles, home files, history and Artifacts are retained." + retention + adoption);
    }

    public async ValueTask<IsolationConfigurationView> ApplyAsync(Guid id, IsolationOwnerRevisions expected, IsolationDraft draft, string fingerprint, CancellationToken ct = default)
    {
        var plan = await PreviewAsync(id, draft, ct);
        if (plan.Expected != expected || plan.Fingerprint != fingerprint) throw AgentCoreErrors.Conflict("State & Isolation changed. Refresh the preview while retaining your draft.");
        if (plan.BlockingReasons.Count > 0) throw AgentCoreErrors.Validation(string.Join(" ", plan.BlockingReasons));
        if (plan.Changes.Count == 0) return await ReadAsync(id, ct);
        var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var next = Patch(owner.SettingsOverrides ?? new(), draft);
        var now = time.GetUtcNow();
        // Reuse the settings audit schema: a bounded list of changed field names, never their private values.
        var audit = new AdminEventAppend(ids.NewId(), now, AdminEventActorKind.LocalOwner, AdminEventOperationKind.InstanceSettingsChanged,
            "agent.instance", id.ToString("D"), expected.Instance + 1, owner.ActiveVersion,
            JsonSerializer.Serialize(new { section = AuditSection,
                set = plan.Changes.Where(c => c.Source == "instance").Select(c => c.Owner + "." + c.Field).ToArray(),
                clear = plan.Changes.Where(c => c.Source == "definition").Select(c => c.Owner + "." + c.Field).ToArray() }));
        await store.ApplyAsync(new(id, expected, next, draft.ExperienceEnabled, draft.AllowMaintenance, audit), now, ct);
        return await ReadAsync(id, ct);
    }

    private static InstanceSettingsOverrides Patch(InstanceSettingsOverrides current, IsolationDraft draft)
    {
        if (draft.Settings is null || draft.Settings.Count > 3 || draft.Settings.Select(p => p.Section).Distinct(StringComparer.Ordinal).Count() != draft.Settings.Count)
            throw AgentCoreErrors.Validation("Use at most one patch per State, Memory and Voice section.");
        foreach (var patch in draft.Settings)
        {
            if (!Sections.Contains(patch.Section) || patch.Section == "voice" && patch.Set.Keys.Concat(patch.Clear).Any(f => f != "enabled"))
                throw AgentCoreErrors.Validation("Only State & Isolation, Memory and Voice enablement are supported by this coordinated operation.");
            current = InstanceSettingsResolver.Patch(current, patch.Section, patch.Set, patch.Clear);
        }
        return current;
    }
}
