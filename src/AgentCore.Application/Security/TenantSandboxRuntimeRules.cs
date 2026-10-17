using System.Text;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantSandboxRuntimeRules
{
    public static void RequireCurrentPolicy(AgentRun run, TenantAgent agent, AgentDefinition baseline, SandboxProfile admitted,
        SandboxProfile source, SandboxMountAuthority? mounted)
    {
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        if (agent.Instance.DefinitionId != run.DefinitionId || !RolePermissions.AllowsTool(current, ToolCatalog.SandboxExec)
            || current.SandboxPolicy?.AllowedProfileIds.Contains(admitted.Id, StringComparer.Ordinal) != true)
            throw new AccessDeniedException("sandbox_policy_changed");
        var setup = SandboxSetupResolver.Resolve(source, agent.Instance.SettingsOverrides, agent.Instance.Revision);
        if (admitted.Cpu > setup.Cpu || admitted.MemoryMiB > setup.MemoryMiB || admitted.Pids > setup.Pids
            || admitted.MaxCommandSeconds > setup.MaxCommandSeconds || admitted.StorageBytes > setup.StorageBytes
            || admitted.IdleTtlSeconds > setup.IdleTtlSeconds || admitted.MaxLifetimeSeconds > setup.MaxLifetimeSeconds
            || admitted.InstanceInitializationScript != setup.InstanceInitializationScript)
            throw new AccessDeniedException("sandbox_setup_changed");
        var isolation = StateIsolationPolicy.Restrict(run.Admission.Configuration!.Definition.StateIsolationPolicy ?? StateIsolationPolicy.Default,
            current.StateIsolationPolicy ?? StateIsolationPolicy.Default);
        if (mounted is not null && mounted.HomeAccess != isolation.HomeAccess)
            throw new AccessDeniedException("sandbox_mount_policy_changed");
    }
    public static async ValueTask RequirePermissionsAsync(TenantSecurityState state, AgentRun run, SandboxProfile profile, IamAuthorizer authorizer, CancellationToken ct)
    {
        var actor = run.Owner.ExecutorPrincipalId!.Value;
        var targets = new List<(string Key, AccessScope Scope)> { ("sandbox.execute", AccessScope.ForResource(state.Tenant.TenantId, "agent", run.AgentInstanceId.ToString("D"))),
            ("sandbox.read", TenantSandboxProfileRules.Scope(state.Tenant.TenantId, profile.Id)) };
        if (profile.RegistryCredentialId is { } credential) targets.Add(("credentials.use", TenantCredentialRules.Scope(state.Tenant.TenantId, credential)));
        foreach (var item in targets)
            if (!(await authorizer.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(item.Key), item.Scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
    }
    public static void RequireReservation(TenantSandboxRuntimeWrite write, TenantSession session, AgentRun run, TenantAgent agent, long securityRevision, DateTimeOffset at)
    {
        TenantRuntimeRules.Require(write.Runtime, session.Owner, run, at);
        if (run.Revision != write.RunRevision || securityRevision != write.SecurityRevision) throw new SecurityConflictException("stale_revision");
        if (agent.TenantId != session.Owner.TenantId || agent.PrincipalId != session.Owner.ExecutorPrincipalId || agent.Instance.InstanceId != session.Owner.AgentInstanceId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || session.Snapshot.ProfileId is not null || session.Snapshot.DurablyDeletedAt is not null
            || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw new AccessDeniedException("tenant_runtime_unavailable");
    }
    public static void ValidateNewLease(TenantSandboxRuntimeWrite write, SandboxLease lease, long expectedRevision, AgentRun run, SandboxLease? previous, DateTimeOffset at)
    {
        var f = write.Runtime;
        ValidateLease(lease);
        var pinned = run.Admission.Configuration?.Definition.ExecutionSandboxProfiles?.SingleOrDefault(p => p.Id == lease.Profile.Id);
        if (lease.TenantId != f.Owner.TenantId || lease.ExecutorPrincipalId != f.Owner.ExecutorPrincipalId || lease.SessionId != f.SessionId || lease.AgentInstanceId != f.Owner.AgentInstanceId
            || lease.Profile.TenantId != lease.TenantId || lease.Generation == Guid.Empty || lease.Revision != checked(expectedRevision + 1)
            || lease.State != SandboxLeaseState.Provisioning || lease.ProviderId is not null || lease.ActiveExecutionId is not null || lease.LastExecutionId is not null
            || lease.AdmissionRunId != f.RunId || lease.AdmissionToolCallId is not { Length: > 0 and <= 128 }
            || lease.CreatedAt > at || lease.ExpiresAt <= at || lease.ExpiresAt <= lease.CreatedAt || lease.Profile.AdmittedForExecution != true || pinned != lease.Profile
            || run.Admission.Configuration?.Definition.SandboxPolicy?.AllowedProfileIds.Contains(lease.Profile.Id, StringComparer.Ordinal) != true
            || !RolePermissions.AllowsTool(run.Admission.Configuration.Definition, ToolCatalog.SandboxExec))
            throw new ArgumentException("Invalid exact tenant Sandbox generation reservation.");
        if (previous is not null && (previous.State != SandboxLeaseState.Terminated || previous.Generation == lease.Generation
            || previous.TenantId != lease.TenantId || previous.AgentInstanceId != lease.AgentInstanceId || previous.ExecutorPrincipalId != lease.ExecutorPrincipalId
            || previous.AdmissionRunId == lease.AdmissionRunId && previous.AdmissionToolCallId == lease.AdmissionToolCallId
            || previous.Initialization?.State is "Running" or "Failed" && previous.CleanupReason != "reset"))
            throw new SecurityConflictException("sandbox_recovery_required");
    }
    public static void ValidateLease(SandboxLease value)
    {
        value.Profile.Validate();
        if (value.TenantId is null || value.TenantId == Guid.Empty || value.ExecutorPrincipalId is null || value.ExecutorPrincipalId == Guid.Empty
            || value.SessionId == Guid.Empty || value.AgentInstanceId == Guid.Empty || value.Generation == Guid.Empty || value.Revision < 1
            || value.Profile.TenantId != value.TenantId || !TenantSandboxProfileRules.ValidId(value.Profile.Id) || value.ExpiresAt <= value.CreatedAt
            || value.ExpiresAt > value.CreatedAt.AddSeconds(value.Profile.MaxLifetimeSeconds) || !Enum.IsDefined(value.State)
            || value.AdmissionRunId is null || value.AdmissionRunId == Guid.Empty || value.AdmissionToolCallId is not { Length: > 0 and <= 128 }
            || value.AdmissionToolCallId.Any(char.IsControl) || value.SafeMessage?.Length > 2000 || value.CleanupPasses < 0
            || value.Initialization is { } init && (init.ProfileId != value.Profile.Id || init.ProfileRevision != value.Profile.Revision || init.InstanceRevision != value.Profile.InstanceRevision
                || init.Stdout is null || init.Stderr is null || Encoding.UTF8.GetByteCount(init.Stdout) > 32768 || Encoding.UTF8.GetByteCount(init.Stderr) > 32768 || init.DurationMs < 0))
            throw new ArgumentException("Invalid bounded tenant Sandbox generation evidence.");
    }
    public static void ValidateNewExecution(TenantSandboxRuntimeWrite write, SandboxExecution execution, SandboxLease lease, AgentRun run, DateTimeOffset at)
    {
        var f = write.Runtime;
        var pinned = run.Admission.Configuration?.Definition.ExecutionSandboxProfiles?.SingleOrDefault(p => p.Id == lease.Profile.Id);
        ValidateExecution(execution);
        if (execution.TenantId != f.Owner.TenantId || execution.ExecutorPrincipalId != f.Owner.ExecutorPrincipalId || execution.SessionId != f.SessionId
            || execution.AgentInstanceId != f.Owner.AgentInstanceId || execution.AgentRunId != f.RunId || execution.ClaimGeneration != f.Generation
            || execution.Revision != 1 || execution.Generation != lease.Generation || execution.ProfileId != lease.Profile.Id || execution.ProfileRevision != lease.Profile.Revision
            || execution.State != SandboxExecutionState.Dispatching || execution.ProviderCommandId is not null || execution.FinishedAt is not null
            || lease.TenantId != execution.TenantId || lease.ExecutorPrincipalId != execution.ExecutorPrincipalId || lease.State != SandboxLeaseState.Ready || lease.ActiveExecutionId is not null
            || execution.DeadlineAt is not { } deadline || deadline <= at || execution.StartedAt > at || deadline <= execution.StartedAt || deadline > lease.ExpiresAt
            || deadline > execution.StartedAt.AddSeconds(lease.Profile.MaxCommandSeconds)
            || pinned?.AdmittedForExecution != true || (pinned with { InstanceRevision = lease.Profile.InstanceRevision }) != lease.Profile
            || !RolePermissions.AllowsTool(run.Admission.Configuration!.Definition, ToolCatalog.SandboxExec))
            throw new ArgumentException("Invalid exact tenant Sandbox execution reservation.");
    }
    public static void ValidateExecution(SandboxExecution value)
    {
        if (value.Id == Guid.Empty || value.TenantId is null || value.TenantId == Guid.Empty || value.ExecutorPrincipalId is null || value.ExecutorPrincipalId == Guid.Empty
            || value.Revision < 1 || value.Generation == Guid.Empty || value.SessionId == Guid.Empty || value.AgentInstanceId == Guid.Empty || value.AgentRunId == Guid.Empty
            || value.ClaimGeneration == Guid.Empty || string.IsNullOrWhiteSpace(value.ToolCallId) || value.ToolCallId.Length > 128 || value.ToolCallId.Any(char.IsControl)
            || value.ActionHash is not { Length: 64 } || value.ActionHash.Any(c => !char.IsAsciiHexDigit(c)) || !Enum.IsDefined(value.State)
            || value.CommandText is null || Encoding.UTF8.GetByteCount(value.CommandText) > 32768 || value.Stdout is null || value.Stderr is null
            || Encoding.UTF8.GetByteCount(value.Stdout) > 32768 || Encoding.UTF8.GetByteCount(value.Stderr) > 32768 || value.DurationMs < 0
            || value.FinishedAt < value.StartedAt || !TenantSandboxProfileRules.ValidId(value.ProfileId) || value.ProfileRevision is not > 0)
            throw new ArgumentException("Invalid bounded tenant Sandbox execution evidence.");
    }
    public static void ValidateLeaseFacts(SandboxLease before, SandboxLease next)
    {
        ValidateLease(next);
        if (before.TenantId != next.TenantId || before.ExecutorPrincipalId != next.ExecutorPrincipalId || before.SessionId != next.SessionId
            || before.AgentInstanceId != next.AgentInstanceId || before.Generation != next.Generation || before.Profile != next.Profile
            || next.Revision != checked(before.Revision + 1) || next.AdmissionRunId != before.AdmissionRunId || next.AdmissionToolCallId != before.AdmissionToolCallId
            || next.CreatedAt != before.CreatedAt || next.ActiveExecutionId is not null && next.ActiveExecutionId != before.ActiveExecutionId
            || before.MountAuthority is not null && before.MountAuthority != next.MountAuthority
            || next.LastExecutionId != before.LastExecutionId || !Enum.IsDefined(next.State)
            || next.CleanupReason == "reset" && before.CleanupReason != "reset"
            || before.State == SandboxLeaseState.Terminated && next.State != SandboxLeaseState.Terminated
            || next.State == SandboxLeaseState.Ready && before.State is SandboxLeaseState.Terminating or SandboxLeaseState.Failed
            || next.State == SandboxLeaseState.Provisioning && before.State != SandboxLeaseState.Provisioning
            || next.State == SandboxLeaseState.Executing && before.State != SandboxLeaseState.Executing
            || before.ProviderId is not null && before.ProviderId != next.ProviderId)
            throw new ArgumentException("Factual settlement cannot replace Sandbox ownership or begin execution.");
    }
    public static void ValidateExecutionFacts(SandboxExecution before, SandboxExecution next)
    {
        ValidateExecution(next);
        if (before.TenantId != next.TenantId || before.ExecutorPrincipalId != next.ExecutorPrincipalId || before.SessionId != next.SessionId || before.AgentInstanceId != next.AgentInstanceId
            || before.Generation != next.Generation || before.Id != next.Id || before.AgentRunId != next.AgentRunId || before.ClaimGeneration != next.ClaimGeneration
            || before.ToolCallId != next.ToolCallId || before.ActionHash != next.ActionHash || before.CommandText != next.CommandText || before.StartedAt != next.StartedAt
            || before.ProfileId != next.ProfileId || before.ProfileRevision != next.ProfileRevision || before.Background != next.Background || before.DeadlineAt != next.DeadlineAt
            || next.Revision != checked(before.Revision + 1) || before.ProviderCommandId is not null && next.ProviderCommandId != before.ProviderCommandId
            || before.FilesystemEffectsMayHaveOccurred && !next.FilesystemEffectsMayHaveOccurred
            || before.RemoteTerminationConfirmed && !next.RemoteTerminationConfirmed || before.GenerationRecycled && !next.GenerationRecycled
            || next.TerminationConfirmed && next.State is SandboxExecutionState.Dispatching or SandboxExecutionState.Running
            || before.TerminationConfirmed && (next.FinishedAt != before.FinishedAt || next.DurationMs != before.DurationMs || next.Truncated != before.Truncated || next.State != before.State || !next.TerminationConfirmed || next.Stdout != before.Stdout || next.Stderr != before.Stderr || next.ExitCode != before.ExitCode))
            throw new ArgumentException("Factual settlement cannot replace or replay a Sandbox operation.");
    }
}
