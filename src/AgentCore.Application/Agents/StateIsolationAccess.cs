using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Agents;

public static class StateIsolationAccess
{
    public static AgentDefinition RestrictDefinition(AgentDefinition admitted, AgentDefinition current)
    {
        var oldMemory = admitted.MemoryPolicy ?? MemoryPolicy.Disabled;
        var nowMemory = current.MemoryPolicy ?? MemoryPolicy.Disabled;
        return admitted with {
            StateIsolationPolicy = StateIsolationPolicy.Restrict(admitted.StateIsolationPolicy ?? StateIsolationPolicy.Default, current.StateIsolationPolicy ?? StateIsolationPolicy.Default),
            MemoryPolicy = new(oldMemory.SessionMemory && nowMemory.SessionMemory,
                oldMemory.IdentityUserPromotion && nowMemory.IdentityUserPromotion, oldMemory.IdentityUserRetrieval && nowMemory.IdentityUserRetrieval,
                oldMemory.UserPromotion && nowMemory.UserPromotion, oldMemory.UserRetrieval && nowMemory.UserRetrieval)
        };
    }

    public static async ValueTask<StateIsolationPolicy> ResolveAsync(AgentDefinition admitted, Guid instanceId,
        IAgentInstanceStore? instances, IAgentDefinitionStore? definitions, CancellationToken ct)
    {
        var pinned = admitted.StateIsolationPolicy ?? StateIsolationPolicy.Default;
        if (instances is null || instanceId == Guid.Empty) return pinned;
        var instance = await instances.FindAsync(instanceId, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var baseline = definitions is null ? admitted : await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct)
            ?? throw AgentCoreErrors.Persistence("Current Definition is unavailable.");
        var current = InstanceSettingsResolver.Resolve(baseline, instance.SettingsOverrides).StateIsolationPolicy ?? StateIsolationPolicy.Default;
        return StateIsolationPolicy.Restrict(pinned, current);
    }

    public static void RequireHome(StateIsolationPolicy policy, string path, bool write)
    {
        if (AgentHomePath.IsHome(path) && (policy.HomeAccess == HomeAccess.Disabled || write && policy.HomeAccess != HomeAccess.ReadWrite))
            throw AgentCoreErrors.Forbidden(write ? "Home writes are disabled by State & Isolation policy. Use this Session's /working."
                : "Home access is disabled by State & Isolation policy. Use this Session's /working.");
    }

    public static string Cwd(StateIsolationPolicy policy, string? cwd) => policy.HomeAccess == HomeAccess.Disabled
        && (cwd is null || AgentHomePath.IsHome(cwd)) ? "/working" : cwd ?? "/home";
}
