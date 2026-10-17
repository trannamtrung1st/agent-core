using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxOperationRules
{
    public static SandboxProfile Profile(AgentRun run, string id) => run.Admission.Configuration?.Definition.ExecutionSandboxProfiles?.SingleOrDefault(p => p.Id == id)
        ?? throw new AccessDeniedException("sandbox_profile_not_admitted");
    public static PreparedSandboxCommand Parameters(AgentRun run, SandboxProfile profile, SandboxLease? lease, SandboxCommandRequest command, HomeAccess home)
    {
        if (command.ProfileId is not null && command.ProfileId != profile.Id) throw new ArgumentException("Conflicting exact Sandbox profile.");
        return new(run.SessionId, profile.Id, profile.Revision, TenantApprovalRules.CanonicalSandboxCommand(command with {
            TimeoutSeconds = command.TimeoutSeconds ?? profile.MaxCommandSeconds, ProfileId = profile.Id }), lease?.Revision ?? 0, lease?.Generation,
            profile.RegistryCredentialId, profile.RegistryCredentialRevision, profile.RegistrySecretRevision, home);
    }
    public static TenantApprovalSnapshot Snapshot(TenantAgent agent, SandboxProfile profile, SandboxLease? lease) =>
        new(agent.Instance.Persona.Name, Sandbox: new(profile.Name, profile.ImageReference, profile.InitializationScript,
            profile.InstanceInitializationScript, lease is null || lease.State == SandboxLeaseState.Terminated, profile.Cpu, profile.MemoryMiB,
            profile.MaxCommandSeconds, profile.IdleTtlSeconds, profile.MaxLifetimeSeconds, profile.StorageBytes));
    public static void Require(PreparedSandboxCommand value, AgentRun run, TenantAgent agent, TenantSession session,
        AgentDefinition baseline, SandboxProfile source, SandboxLease? lease, DateTimeOffset at)
    {
        var command = TenantApprovalRules.SandboxCommand(value); var profile = Profile(run, value.ProfileId);
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        var home = StateIsolationPolicy.Restrict(run.Admission.Configuration!.Definition.StateIsolationPolicy ?? StateIsolationPolicy.Default,
            current.StateIsolationPolicy ?? StateIsolationPolicy.Default).HomeAccess;
        if (value.SessionId != run.SessionId || session.Owner.TenantId != run.Owner.TenantId || session.Owner.AgentInstanceId != run.AgentInstanceId
            || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || session.Snapshot.DurablyDeletedAt is not null
            || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || profile.AdmittedForExecution != true || !RolePermissions.AllowsTool(run.Admission.Configuration.Definition, ToolCatalog.SandboxExec)
            || value.ProfileRevision != profile.Revision || command.TimeoutSeconds > profile.MaxCommandSeconds
            || value.RegistryCredentialId != profile.RegistryCredentialId || value.RegistryCredentialRevision != profile.RegistryCredentialRevision
            || value.RegistrySecretRevision != profile.RegistrySecretRevision || value.HomeAccess != home
            || (lease?.Revision ?? 0) != value.ExpectedLeaseRevision || lease?.Generation != value.ExpectedGeneration)
            throw new SecurityConflictException("sandbox_operation_witness_changed");
        TenantSandboxRuntimeRules.RequireCurrentPolicy(run, agent, baseline, profile, source, lease?.MountAuthority);
        StateIsolationAccess.RequireHome(new(HomeAccess: home), command.Cwd, false);
        if (lease is not null && (lease.State == SandboxLeaseState.Ready && lease.ExpiresAt <= at
            || lease.State == SandboxLeaseState.Terminated && lease.Initialization?.State is "Running" or "Failed" && lease.CleanupReason != "reset"
            || lease.State is not (SandboxLeaseState.Ready or SandboxLeaseState.Terminated) || lease.ActiveExecutionId is not null
            || lease.State == SandboxLeaseState.Ready && (lease.Profile with { InstanceRevision = profile.InstanceRevision }) != profile))
            throw new AccessDeniedException("sandbox_recovery_required");
    }
}
