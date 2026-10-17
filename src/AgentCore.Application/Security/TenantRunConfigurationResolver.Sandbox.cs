using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunConfigurationResolver
{
    private async ValueTask<AgentDefinition> BindSandboxProfilesAsync(Guid tenant, Ports.TenantAgent agent, AgentDefinition definition, CancellationToken ct)
    {
        var pinned = new List<SandboxProfile>();
        if (sandboxProfiles is not null && sandboxImages is not null && authorizer is not null)
            foreach (var id in definition.SandboxPolicy?.AllowedProfileIds ?? [])
            {
                if (!(await authorizer.CheckAsync(agent.PrincipalId, "sandbox.read", TenantSandboxProfileRules.Scope(tenant, id), ct).ConfigureAwait(false)).Allowed) continue;
                var profile = await sandboxProfiles.FindTenantSandboxProfileAsync(tenant, id, ct: ct).ConfigureAwait(false);
                if (profile is null) continue;
                var available = await sandboxImages.InspectAsync(profile, ct).ConfigureAwait(false);
                var setup = SandboxSetupResolver.Resolve(profile, agent.Instance.SettingsOverrides, agent.Instance.Revision);
                pinned.Add(setup with { AdmittedForExecution = available.Eligible && sandboxReadiness?.State == "Ready",
                    RegistryCredentialRevision = available.CredentialRevision, RegistrySecretRevision = available.SecretRevision });
            }
        return definition with { ExecutionSandboxProfiles = pinned.ToArray(), ExecutionSandboxProfile = pinned.FirstOrDefault(p => p.Id == definition.SandboxPolicy?.DefaultProfileId) };
    }
}
