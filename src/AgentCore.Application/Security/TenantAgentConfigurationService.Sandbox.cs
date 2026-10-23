using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantAgentSandboxProfile(string Id, long Revision, string Name, string Description, string ImageReference, string Availability, string Message);
public sealed record TenantAgentSandboxSetup(Guid InstanceId, long InstanceRevision, AgentInstanceLifecycle Lifecycle, InstanceSettingsSection? Section, IReadOnlyList<TenantAgentSandboxProfile> Profiles,
    bool CompleteCatalog, bool CanManage, long SecurityRevision, string InstanceName, int DefinitionVersion);

public sealed partial class TenantAgentConfigurationService
{
    public async ValueTask<TenantAgentSandboxSetup> ReadSandboxAsync(Guid actor, Guid tenant, Guid id, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, "sandbox.read", Scope(tenant, id), ct).ConfigureAwait(false);
        var (snapshot, definition) = await BoundForRead(actor, tenant, id, ct).ConfigureAwait(false);
        if (sandboxProfiles is null || sandboxImages is null) throw new AccessDeniedException("resource_unavailable");
        var items = new List<TenantAgentSandboxProfile>(); var readable = new Dictionary<string, SandboxProfile>(StringComparer.Ordinal);
        foreach (var profileId in definition.SandboxPolicy?.AllowedProfileIds ?? [])
        {
            var access = await authorizer.CheckAsync(actor, "sandbox.read", TenantSandboxProfileRules.Scope(tenant, profileId), ct).ConfigureAwait(false);
            if (!access.Allowed) continue;
            var profile = await sandboxProfiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false);
            if (profile is null) continue; // Never resolve a configured ID through the global catalog.
            readable.Add(profile.Id, profile);
            var availability = await SandboxAvailability(profile, ct).ConfigureAwait(false);
            items.Add(new(profile.Id, profile.Revision, profile.Name, profile.Description, profile.ImageReference, availability.Status, availability.Message));
        }
        var complete = readable.Count == (definition.SandboxPolicy?.AllowedProfileIds.Count ?? 0);
        var policy = InstanceSettingsResolver.Resolve(definition, snapshot.Agent.Instance.SettingsOverrides).SandboxPolicy;
        var selected = policy?.DefaultProfileId;
        var bound = definition with { ExecutionSandboxProfile = selected is null ? null : readable.GetValueOrDefault(selected), ExecutionSandboxProfiles = null };
        // Partial catalogs do not project hidden profile IDs, defaults, ceilings or scripts.
        var section = policy?.AllowedProfileIds.All(readable.ContainsKey) != false ? InstanceSettingsRules.View(snapshot.Agent.Instance, bound, "sandboxPolicy") : null;
        if (section is not null && !complete)
        {
            var defaults = section.DefinitionDefaults.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            defaults["allowedProfileIds"] = readable.Keys.ToArray();
            if (definition.SandboxPolicy is { } inherited && !readable.ContainsKey(inherited.DefaultProfileId)) defaults["profileId"] = null;
            var constraints = section.Constraints.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var key in new[] { "allowedProfileIds", "profileId" }) constraints[key] = constraints[key] with { Choices = readable.Keys.ToArray() };
            section = section with { DefinitionDefaults = defaults, Constraints = constraints };
        }
        var canManage = snapshot.Agent.Instance.Lifecycle == AgentInstanceLifecycle.Active
            && (await authorizer.CheckAsync(actor, "agents.update", Scope(tenant, id), ct).ConfigureAwait(false)).Allowed
            && (await authorizer.CheckAsync(actor, "sandbox.manage", Scope(tenant, id), ct).ConfigureAwait(false)).Allowed;
        return new(id, snapshot.Agent.Instance.Revision, snapshot.Agent.Instance.Lifecycle, section, items, complete, canManage, await ReadRevision(actor, tenant, id, ct).ConfigureAwait(false), snapshot.Agent.Instance.Persona.Name, snapshot.Agent.Instance.ActiveVersion);
    }

    private async ValueTask<AgentDefinition> PrepareSandboxPatch(Guid actor, Guid tenant, AgentInstance current, AgentDefinition definition,
        IReadOnlyDictionary<string, JsonElement> set, IReadOnlyList<string> clear, CancellationToken ct)
    {
        if (sandboxProfiles is null || sandboxImages is null) throw new AccessDeniedException("resource_unavailable");
        if (set.TryGetValue("profileId", out var chosen) && chosen.ValueKind == JsonValueKind.String && !TenantSandboxProfileRules.ValidId(chosen.GetString()))
            throw new AccessDeniedException("resource_unavailable");
        var previous = InstanceSettingsResolver.Resolve(definition, current.SettingsOverrides).SandboxPolicy;
        var candidate = InstanceSettingsResolver.Patch(current.SettingsOverrides ?? new(), "sandboxPolicy", set, clear);
        var effective = InstanceSettingsResolver.Resolve(definition, candidate);
        SandboxProfile? selected = null;
        foreach (var profileId in effective.SandboxPolicy?.AllowedProfileIds ?? [])
        {
            await authorizer.RequireAsync(actor, "sandbox.read", TenantSandboxProfileRules.Scope(tenant, profileId), ct).ConfigureAwait(false);
            var profile = await sandboxProfiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false)
                ?? throw new AccessDeniedException("resource_unavailable");
            // Every selectable profile caps the shared Instance overrides, independently of the default.
            _ = SandboxSetupResolver.Resolve(profile, candidate, current.Revision, rejectExpansion: true);
            var newlyAllowed = previous?.AllowedProfileIds.Contains(profileId, StringComparer.Ordinal) != true;
            if (newlyAllowed || (set.ContainsKey("profileId") || previous?.DefaultProfileId != effective.SandboxPolicy?.DefaultProfileId)
                && profileId == effective.SandboxPolicy?.DefaultProfileId)
                if (!(await SandboxAvailability(profile, ct).ConfigureAwait(false)).Eligible)
                    throw AgentCoreErrors.Validation("Choose a currently verified authorized profile; repair and verify it in Sandboxes first.");
            if (profile.Id == effective.SandboxPolicy?.DefaultProfileId) selected = profile;
        }
        return definition with { ExecutionSandboxProfile = selected, ExecutionSandboxProfiles = null };
    }

    private async ValueTask<SandboxImageAvailability> SandboxAvailability(SandboxProfile profile, CancellationToken ct)
    {
        var value = await sandboxImages!.InspectAsync(profile, ct).ConfigureAwait(false);
        return value.Eligible && sandboxReadiness?.State != "Ready"
            ? value with { Eligible = false, Status = "ServerUnavailable", Message = "The saved profile is verified, but the private Sandbox server is unavailable or disabled." } : value;
    }
}
