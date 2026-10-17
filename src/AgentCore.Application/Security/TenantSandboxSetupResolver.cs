using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxSetupSnapshot(TenantAgent Agent, SandboxProfile Profile);

// Trusted owning-source resolution only. Callers must authorize the human/agent and exact profile.
// No image auth, SDK dispatch or global durable source fallback belongs in this resolver.
public sealed class TenantSandboxSetupResolver(ITenantAgentStore agents, ITenantDefinitionStore definitions,
    IBuiltInAgentDefinitionStore builtIns, ITenantSandboxProfileStore profiles)
{
    public async ValueTask<TenantSandboxSetupSnapshot> ResolveAsync(Guid tenant, Guid instanceId, string profileId, CancellationToken ct = default)
    {
        var agent = await agents.FindTenantAgentAsync(tenant, instanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var instance = agent.Instance;
        if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("resource_unavailable");
        var definition = Guid.TryParse(instance.DefinitionId, out var owned)
            ? (await definitions.FindDefinitionPublicationAsync(tenant, owned, instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct).ConfigureAwait(false);
        if (definition is null) throw new AccessDeniedException("definition_unavailable");
        var effective = InstanceSettingsResolver.Resolve(definition, instance.SettingsOverrides);
        if (effective.SandboxPolicy?.AllowedProfileIds.Contains(profileId, StringComparer.Ordinal) != true) throw new AccessDeniedException("resource_unavailable");
        var profile = await profiles.FindTenantSandboxProfileAsync(tenant, profileId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        return new(agent, SandboxSetupResolver.Resolve(profile, instance.SettingsOverrides, instance.Revision, rejectExpansion: true));
    }
}
