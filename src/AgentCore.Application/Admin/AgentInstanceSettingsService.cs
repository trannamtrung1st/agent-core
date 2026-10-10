using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public sealed record InstanceSettingsSection(string Section, long InstanceRevision, string DefinitionId, int DefinitionVersion,
    IReadOnlyDictionary<string, object?> Overrides, IReadOnlyDictionary<string, object?> Effective,
    IReadOnlyDictionary<string, string> Sources, string ConfigurationHash, IReadOnlyDictionary<string, object?> DefinitionDefaults,
    IReadOnlyDictionary<string, InstanceSettingConstraint> Constraints);

public sealed class AgentInstanceSettingsService(IAgentInstanceStore instances, IAgentDefinitionStore definitions,
    IIdGenerator ids, TimeProvider time, IModelCatalog? models = null, ProviderAliasSet? aliases = null,
    Triggers.InstanceAutomationPolicy? automationPolicy = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async ValueTask<IReadOnlyList<InstanceSettingsSection>> ReadAllAsync(Guid id, CancellationToken ct = default)
    {
        var owner = await RequireAsync(id, ct);
        var definition = await DefinitionAsync(owner, ct);
        return InstanceSettingsResolver.Sections.Select(section => View(owner, definition, section)).ToArray();
    }
    public async ValueTask<InstanceSettingsSection> ReadAsync(Guid id, string section, CancellationToken ct = default)
    {
        var owner = await RequireAsync(id, ct);
        return View(owner, await DefinitionAsync(owner, ct), section);
    }

    public async ValueTask<InstanceSettingsSection> PatchAsync(Guid id, string section, long expectedRevision,
        IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear, CancellationToken ct = default,
        AdminEventActorKind actor = AdminEventActorKind.LocalOwner, HarnessManagementState? harnessManagement = null)
    {
        var owner = await RequireAsync(id, ct);
        if (owner.Lifecycle != AgentInstanceLifecycle.Active) throw AgentCoreErrors.Validation("Archived instances are read-only.");
        if (owner.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Instance revision is stale. Reload while retaining your draft.");
        var definition = await DefinitionAsync(owner, ct);
        var current = owner.SettingsOverrides ?? new();
        var next = InstanceSettingsResolver.Patch(current, section, set, clear);
        var effective = InstanceSettingsResolver.Resolve(definition, next);
        if (models is not null) _ = SessionModelBinder.PinDefault(models, effective);
        if (aliases is not null && (!aliases.LanguageModels.Contains(effective.ProviderPreferences.LanguageModel)
            || effective.ProviderPreferences.SpeechRecognizer is { } stt && !aliases.SpeechRecognizers.Contains(stt)
            || effective.ProviderPreferences.SpeechSynthesizer is { } tts && !aliases.SpeechSynthesizers.Contains(tts)))
            throw AgentCoreErrors.Validation("Provider alias is not configured on this host.");
        DefinitionResourcePolicies.RejectSecretTokens(effective.SystemInstructions);
        if (JsonSerializer.Serialize(current, Json) == JsonSerializer.Serialize(next, Json)) return View(owner, definition, section);
        var now = time.GetUtcNow();
        var history = new AdminEventAppend(ids.NewId(), now, actor, AdminEventOperationKind.InstanceSettingsChanged,
            "agent.instance", id.ToString("D"), owner.Revision + 1, definition.Version,
            JsonSerializer.Serialize(new { section, set = set.Keys.ToArray(), clear }));
        var reconciliation = section == "triggerPolicy" && automationPolicy is not null ? await automationPolicy.PlanAsync(owner, effective, now, ct) : null;
        var updated = await instances.UpdateWithExpectedRevisionAsync(new(id, expectedRevision,
            History: history, SetSettingsOverrides: true, SettingsOverrides: next, AutomationPolicyChanges: reconciliation, HarnessManagement: harnessManagement), now, ct);
        return View(updated, definition, section);
    }

    private static InstanceSettingsSection View(AgentInstance owner, AgentDefinition definition, string section)
    {
        var overrides = InstanceSettingsResolver.Overrides(owner.SettingsOverrides ?? new(), section);
        var effective = InstanceSettingsResolver.Resolve(definition, owner.SettingsOverrides);
        return new(section, owner.Revision, definition.Id, definition.Version, overrides,
            InstanceSettingsResolver.Values(effective, section), InstanceSettingsResolver.Fields(section).ToDictionary(
                f => f, f => overrides.ContainsKey(f) ? "instance" : "definition", StringComparer.Ordinal),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { definition = effective, owner.Revision }, Json))).ToLowerInvariant(),
            InstanceSettingsResolver.Values(definition, section), InstanceSettingsResolver.Constraints(definition, section));
    }
    private async ValueTask<AgentInstance> RequireAsync(Guid id, CancellationToken ct) =>
        await instances.FindAsync(id, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
    private async ValueTask<AgentDefinition> DefinitionAsync(AgentInstance owner, CancellationToken ct) =>
        await definitions.GetAsync(owner.DefinitionId, owner.ActiveVersion, ct) ?? throw AgentCoreErrors.NotFound("Selected Definition was not found.");
}
