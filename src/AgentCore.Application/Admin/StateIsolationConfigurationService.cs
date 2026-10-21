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
    bool ExperienceEnabled, bool AllowMaintenance, string HostBrowserMode, bool BrowserEnabled, string SandboxEnforcementState = "Enforced");
public sealed record IsolationChangePlan(IsolationOwnerRevisions Expected, IsolationDraft Draft, string Fingerprint,
    IReadOnlyList<IsolationChange> Changes, IReadOnlyList<string> BlockingReasons, string Notice);

public sealed class StateIsolationConfigurationService(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IExperienceStore experience, AgentInstanceSettingsService settings, IStateIsolationConfigurationStore store,
    IBrowser browser, IIdGenerator ids, TimeProvider time, SandboxPolicyEnforcement? sandboxPolicy = null)
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
            learned.Enabled, maintenance.AllowAgentConsolidation, browser.HostPolicy.ProfileMode.ToString(), browser.HostPolicy.Enabled, sandboxPolicy is null ? "Enforced" : await sandboxPolicy.ObserveAsync(id, ct));
    }

    public async ValueTask<IsolationChangePlan> PresetAsync(Guid id, string preset, CancellationToken ct = default)
    {
        var view = await ReadAsync(id, ct);
        var plan = await PreviewAsync(id, IsolationConfigurationPlanner.Preset(view, preset), ct);
        if (plan.Expected != view.Revisions) throw AgentCoreErrors.Conflict("State & Isolation changed while preparing the preset. Retry.");
        return plan;
    }

    public async ValueTask<IsolationChangePlan> PreviewAsync(Guid id, IsolationDraft draft, CancellationToken ct = default)
    {
        var view = await ReadAsync(id, ct);
        var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        if (owner.Revision != view.Revisions.Instance) throw AgentCoreErrors.Conflict("Instance changed while preparing preview.");
        var baseline = await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.Persistence("Definition is unavailable.");
        return await IsolationConfigurationPlanner.PreviewAsync(id, view, owner, baseline, draft, browser, time, ct);
    }

    public async ValueTask<IsolationConfigurationView> ApplyAsync(Guid id, IsolationOwnerRevisions expected, IsolationDraft draft, string fingerprint, CancellationToken ct = default)
    {
        var plan = await PreviewAsync(id, draft, ct);
        if (plan.Expected != expected || plan.Fingerprint != fingerprint) throw AgentCoreErrors.Conflict("State & Isolation changed. Refresh the preview while retaining your draft.");
        if (plan.BlockingReasons.Count > 0) throw AgentCoreErrors.Validation(string.Join(" ", plan.BlockingReasons));
        if (plan.Changes.Count == 0) return await ReadAsync(id, ct);
        var owner = await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var next = IsolationConfigurationPlanner.Patch(owner.SettingsOverrides ?? new(), draft);
        var now = time.GetUtcNow();
        // Reuse the settings audit schema: a bounded list of changed field names, never their private values.
        var audit = new AdminEventAppend(ids.NewId(), now, AdminEventActorKind.LocalOwner, AdminEventOperationKind.InstanceSettingsChanged,
            "agent.instance", id.ToString("D"), expected.Instance + 1, owner.ActiveVersion,
            JsonSerializer.Serialize(new { section = AuditSection,
                set = plan.Changes.Where(c => c.Source == "instance").Select(c => c.Owner + "." + c.Field).ToArray(),
                clear = plan.Changes.Where(c => c.Source == "definition").Select(c => c.Owner + "." + c.Field).ToArray() }));
        await store.ApplyAsync(new(id, expected, next, draft.ExperienceEnabled, draft.AllowMaintenance, audit), now, ct);
        if (sandboxPolicy is not null) await sandboxPolicy.EnforceAsync(id, ct);
        return await ReadAsync(id, ct);
    }

}