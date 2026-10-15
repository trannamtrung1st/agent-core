using AgentCore.Domain.Definitions;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Agents;

/// <summary>Resolve sparse Instance values without expanding Global profile ceilings or tool authority.</summary>
public static class SandboxSetupResolver
{
    public static SandboxPolicy? ResolvePolicy(SandboxPolicy? baseline, InstanceSettingsOverrides settings)
    {
        if (baseline is null)
        {
            if (settings.SandboxAllowedProfileIds is not null || settings.SandboxProfileId is not null)
                throw AgentCoreErrors.Validation("This Definition does not authorize Sandbox profiles.");
            return null;
        }
        var allowed = settings.SandboxAllowedProfileIds?.Value ?? baseline.AllowedProfileIds;
        if (allowed.Any(id => !baseline.AllowedProfileIds.Contains(id, StringComparer.Ordinal)))
            throw AgentCoreErrors.Validation("Instance allowed profiles must be a subset of the Definition list.");
        var policy = new SandboxPolicy(settings.SandboxProfileId?.Value ?? baseline.DefaultProfileId, allowed.ToArray());
        try { policy.Validate(); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Choose 1–16 distinct authorized profiles and a default in that allowed set."); }
        return policy;
    }

    public static void ValidateOverrides(InstanceSettingsOverrides settings)
    {
        // Host bounds and text validity still apply when an authorized Profile
        // has not been configured yet. Validate only known sparse relationships.
        var candidate = new SandboxProfile("validation", 1, "Validation", "", true, "sha256:" + new string('0',64),
            Cpu:settings.SandboxCpu?.Value ?? 1,MemoryMiB:settings.SandboxMemoryMiB?.Value ?? 1024,
            MaxCommandSeconds:settings.SandboxMaxCommandSeconds?.Value ?? 120,
            IdleTtlSeconds:settings.SandboxIdleTtlSeconds?.Value ?? 60,MaxLifetimeSeconds:settings.SandboxMaxLifetimeSeconds?.Value ?? 86400,
            StorageBytes:settings.SandboxStorageBytes?.Value ?? 250L*1024*1024,InstanceInitializationScript:settings.SandboxInitializationScript?.Value);
        try { candidate.Validate(); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Use bounded Sandbox overrides and valid initialization text."); }
    }

    public static SandboxProfile Resolve(SandboxProfile profile, InstanceSettingsOverrides? settings, long? instanceRevision, bool rejectExpansion = false)
    {
        var o = settings ?? new();
        var resolved = profile with {
            Cpu = o.SandboxCpu?.Value ?? profile.Cpu,
            MemoryMiB = o.SandboxMemoryMiB?.Value ?? profile.MemoryMiB,
            MaxCommandSeconds = o.SandboxMaxCommandSeconds?.Value ?? profile.MaxCommandSeconds,
            IdleTtlSeconds = o.SandboxIdleTtlSeconds?.Value ?? profile.IdleTtlSeconds,
            MaxLifetimeSeconds = o.SandboxMaxLifetimeSeconds?.Value ?? profile.MaxLifetimeSeconds,
            StorageBytes = o.SandboxStorageBytes?.Value ?? profile.StorageBytes,
            InstanceInitializationScript = o.SandboxInitializationScript?.Value,
            InstanceRevision = instanceRevision
        };
        if (rejectExpansion && (resolved.Cpu > profile.Cpu || resolved.MemoryMiB > profile.MemoryMiB || resolved.MaxCommandSeconds > profile.MaxCommandSeconds
            || resolved.IdleTtlSeconds > profile.IdleTtlSeconds || resolved.MaxLifetimeSeconds > profile.MaxLifetimeSeconds || resolved.StorageBytes > profile.StorageBytes))
            throw AgentCoreErrors.Validation("Instance Sandbox limits cannot exceed the selected Global Profile ceilings.");
        resolved = resolved with { Cpu = Math.Min(resolved.Cpu, profile.Cpu), MemoryMiB = Math.Min(resolved.MemoryMiB, profile.MemoryMiB),
            MaxCommandSeconds = Math.Min(resolved.MaxCommandSeconds, profile.MaxCommandSeconds), IdleTtlSeconds = Math.Min(resolved.IdleTtlSeconds, profile.IdleTtlSeconds),
            MaxLifetimeSeconds = Math.Min(resolved.MaxLifetimeSeconds, profile.MaxLifetimeSeconds), StorageBytes = Math.Min(resolved.StorageBytes, profile.StorageBytes) };
        if (!rejectExpansion) resolved = resolved with { IdleTtlSeconds = Math.Min(resolved.IdleTtlSeconds, resolved.MaxLifetimeSeconds) };
        try { resolved.Validate(); }
        catch (ArgumentException) { throw AgentCoreErrors.Validation("Use bounded Sandbox limits, valid initialization text, and a maximum lifetime at least as long as idle expiry."); }
        return resolved;
    }
}
