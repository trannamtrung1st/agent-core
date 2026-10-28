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

internal static class InstanceSettingsRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static void ValidateBrowser(AgentDefinition effective, IBrowser? browser)
    {
        if (effective.StateIsolationPolicy?.BrowserIsolation == BrowserIsolation.PersistentAgent
            && browser?.HostPolicy.ProfileMode != BrowserProfileMode.PersistentAgent)
            throw AgentCoreErrors.Validation("This host does not permit persistent browser profiles.");
    }

    internal static (InstanceSettingsOverrides Next, AgentDefinition Effective) ResolvePatch(AgentDefinition definition,
        InstanceSettingsOverrides current, string section, IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear, IModelCatalog? models, ProviderAliasSet? aliases, bool managed = false)
    {
        var next = InstanceSettingsResolver.Patch(current, section, set, clear);
        var effective = InstanceSettingsResolver.Resolve(definition, next, managed);
        if (models is not null && effective.ModelDefaults is not { Mode: "auto" }
            && effective.ModelDefaults?.Reasoning?.Mode != "auto") _ = SessionModelBinder.BindPreference(models, effective.ModelDefaults ?? AgentCore.Domain.Models.ModelPreference.Inherit);
        if (aliases is not null && (effective.ProviderPreferences.SpeechRecognizer is { } stt && !aliases.SpeechRecognizers.Contains(stt)
            || effective.ProviderPreferences.SpeechSynthesizer is { } tts && !aliases.SpeechSynthesizers.Contains(tts)))
            throw AgentCoreErrors.Validation("Provider alias is not configured on this host.");
        DefinitionResourcePolicies.RejectSecretTokens(effective.AgentInstructions);
        return (next, effective);
    }
    internal static InstanceSettingsSection WithHostConstraints(InstanceSettingsSection view, IBrowser? browser)
    {
        if (view.Section != "stateIsolationPolicy" || browser?.HostPolicy.ProfileMode == BrowserProfileMode.PersistentAgent) return view;
        var constraints = view.Constraints.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var profile = constraints["browserIsolation"];
        constraints["browserIsolation"] = profile with { Reason = "This host permits ephemeral Session contexts. Persistent profiles require a deployment that permits them.",
            Choices = profile.Choices?.Where(c => c != "PersistentAgent").ToArray() };
        return view with { Constraints = constraints };
    }
    internal static InstanceSettingsSection View(AgentInstance owner, AgentDefinition definition, string section, bool managed = false)
    {
        var overrides = InstanceSettingsResolver.Overrides(owner.SettingsOverrides ?? new(), section);
        var effective = InstanceSettingsResolver.Resolve(definition, owner.SettingsOverrides, managed);
        if (managed && section == "instructions")
        {
            effective = effective with { AgentInstructions = owner.SettingsOverrides?.AgentInstructions?.Value ?? "" };
            definition = definition with { AgentInstructions = "" };
        }
        return new(section, owner.Revision, definition.Id, definition.Version, overrides,
            InstanceSettingsResolver.Values(effective, section), InstanceSettingsResolver.Fields(section).ToDictionary(
                f => f, f => overrides.ContainsKey(f) ? "instance" : section == "sandboxPolicy" && f is not ("profileId" or "allowedProfileIds") ? "profile" : "definition", StringComparer.Ordinal),
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { definition = effective, owner.Revision }, Json))).ToLowerInvariant(),
            InstanceSettingsResolver.Values(definition, section), InstanceSettingsResolver.Constraints(definition, section));
    }
}
