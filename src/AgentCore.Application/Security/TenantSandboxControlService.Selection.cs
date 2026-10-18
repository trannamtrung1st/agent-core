using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxProfileSwitchReceipt(Guid SessionId, Guid Generation, string ProfileId, long ProfileRevision,
    long InstanceRevision, long LeaseRevision, long SessionRevision, long SecurityRevision, bool RetirementConfirmed);

public sealed partial class TenantSandboxControlService
{
    public async ValueTask<TenantSandboxProfileSwitchReceipt> RequestProfileSwitchAsync(Guid tenant, HumanSessionReference human,
        TenantSandboxProfileSwitch change, long expectedSecurityRevision, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, change.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, null, human, authorizer, ct).ConfigureAwait(false);
        if (!TenantSandboxProfileRules.ValidId(change.ProfileId)) throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, change.ProfileId, human, authorizer, ct).ConfigureAwait(false);
        var saved = await storage.ReadTenantSandboxRuntimeAsync(tenant, change.SessionId, ct: ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = saved.Lease ?? throw new AccessDeniedException("resource_unavailable");
        await TenantSandboxControlRules.RequirePermissionsAsync(state, session.Owner, lease.Profile.Id, human, authorizer, ct).ConfigureAwait(false);
        if (setups is null || images is null || host is null || readiness?.State != "Ready") throw new AccessDeniedException("sandbox_selection_unavailable");
        var setup = await setups.ResolveAsync(tenant, session.Owner.AgentInstanceId, change.ProfileId, ct).ConfigureAwait(false);
        var available = await images.InspectAsync(setup.Profile, ct).ConfigureAwait(false);
        if (!available.Eligible) throw new AccessDeniedException("sandbox_source_unavailable");
        var target = setup.Profile with { RegistryCredentialRevision = available.CredentialRevision, RegistrySecretRevision = available.SecretRevision };
        var prepared = new TenantSandboxProfileSwitchPreparation(change, target, host.Scope);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || current.Tenant.Revision != expectedSecurityRevision || saved.Owner != session.Owner
            || saved.SessionRevision != session.Snapshot.Revision) throw new SecurityConflictException("stale_revision");
        TenantSandboxControlRules.RequireSelection(prepared, session, lease, saved.Execution, setup.Agent);
        var next = state with { Tenant = state.Tenant with { Revision = checked(expectedSecurityRevision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "sandbox.profile.switch", change.SessionId.ToString("D"), "allowed", "human_profile_switch", time.GetUtcNow());
        var requested = await storage.TryRequestTenantSandboxProfileSwitchAsync(next, expectedSecurityRevision, prepared, human, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        return new(change.SessionId, requested.Generation, target.Id, target.Revision, target.InstanceRevision!.Value, requested.Revision,
            saved.SessionRevision, next.Tenant.Revision, requested.State == SandboxLeaseState.Terminated);
    }
}

public static partial class TenantSandboxControlRules
{
    public static void RequireSelection(TenantSandboxProfileSwitchPreparation prepared, TenantSession session, SandboxLease lease, SandboxExecution? execution, TenantAgent agent)
    {
        var c = prepared.Change; var target = prepared.Target;
        Require(new TenantSandboxReset(c.SessionId, c.ExpectedSessionRevision, c.ExpectedGeneration, c.ExpectedLeaseRevision, c.ExpectedExecutionId), session, lease);
        if (c.ExpectedInstanceRevision < 1 || c.ExpectedProfileRevision < 1 || target.Id != c.ProfileId || target.Revision != c.ExpectedProfileRevision
            || target.InstanceRevision != c.ExpectedInstanceRevision || agent.Instance.Revision != c.ExpectedInstanceRevision) throw new SecurityConflictException("stale_revision");
        if (agent.TenantId != session.Owner.TenantId || agent.PrincipalId != session.Owner.ExecutorPrincipalId || agent.Instance.InstanceId != session.Owner.AgentInstanceId
            || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending || target.TenantId != agent.TenantId || !target.Enabled)
            throw new AccessDeniedException("resource_unavailable");
        if (lease.ActiveExecutionId is not null || lease.State is not (SandboxLeaseState.Ready or SandboxLeaseState.Terminated)
            || execution is { TerminationConfirmed: false } || lease.Initialization?.State is "Running" or "Failed" && lease.CleanupReason != "reset")
            throw new SecurityConflictException("sandbox_recovery_required");
        if (!ValidHost(prepared.HostScope) || lease.State != SandboxLeaseState.Terminated && lease.ProviderHostScope != prepared.HostScope)
            throw new AccessDeniedException("sandbox_host_scope_changed");
        var same = lease.SelectionWitness is { } old ? lease.TransitionProfileId == target.Id && lease.TransitionProfileRevision == target.Revision
            && old.InstanceRevision == target.InstanceRevision && old.HostScope == prepared.HostScope && old.RegistryCredentialId == target.RegistryCredentialId
            && old.RegistryCredentialRevision == target.RegistryCredentialRevision && old.RegistrySecretRevision == target.RegistrySecretRevision
            : lease.State == SandboxLeaseState.Ready && (lease.Profile with { AdmittedForExecution = target.AdmittedForExecution }) == target;
        if (same) throw new SecurityConflictException("sandbox_profile_already_selected");
    }
    public static void RequireSelectionSource(TenantSandboxProfileSwitchPreparation prepared, TenantAgent agent, AgentDefinition baseline,
        SandboxProfile source, SandboxVerificationEvidence evidence)
    {
        var effective = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        if (!RolePermissions.AllowsTool(effective, ToolCatalog.SandboxExec) || effective.SandboxPolicy?.AllowedProfileIds.Contains(source.Id, StringComparer.Ordinal) != true)
            throw new AccessDeniedException("sandbox_policy_changed");
        TenantSandboxVerificationRules.RequireCurrent(prepared.Target, SandboxSetupResolver.Resolve(source, agent.Instance.SettingsOverrides, agent.Instance.Revision, rejectExpansion: true));
        var v = evidence.Observation;
        if (evidence.Pending || v is not { Passed: true, IncludesInitialization: true, InstanceId: null, InstanceRevision: null }
            || v.TenantId != agent.TenantId || v.ProfileId != source.Id || v.ProfileRevision != source.Revision || v.ContractVersion != SandboxProfileVerification.CurrentContractVersion
            || v.HostScope != prepared.HostScope || v.RegistryCredentialId != prepared.Target.RegistryCredentialId
            || v.RegistryCredentialRevision != prepared.Target.RegistryCredentialRevision || v.RegistrySecretRevision != prepared.Target.RegistrySecretRevision)
            throw new AccessDeniedException("sandbox_source_unavailable");
    }
    public static SandboxLease ApplySelection(SandboxLease lease, TenantSandboxProfileSwitchPreparation prepared, AuthorizationAudit audit)
    {
        var target = prepared.Target;
        var next = lease with { Revision = checked(lease.Revision + 1), SelectedProfileId = target.Id, TransitionProfileId = target.Id, TransitionProfileRevision = target.Revision,
            SelectionWitness = new(audit.AuditId, target.InstanceRevision!.Value, target.RegistryCredentialId, target.RegistryCredentialRevision, target.RegistrySecretRevision, prepared.HostScope),
            State = lease.State == SandboxLeaseState.Terminated ? SandboxLeaseState.Terminated : SandboxLeaseState.Terminating,
            CleanupReason = lease.CleanupReason == "reset" ? "reset" : "profileSwitch", TransitionPhase = lease.State == SandboxLeaseState.Terminated ? "awaitingReview" : "retirement",
            SafeMessage = lease.State == SandboxLeaseState.Terminated ? "Profile selection saved. A fresh exact command Review is required before initialization. Canonical files are retained."
                : "Profile switch requested. Old generation retirement is pending; no replacement or initialization has started. Canonical files are retained." };
        TenantSandboxRuntimeRules.ValidateLease(next); return next;
    }
    public static void SelectionAudit(Guid tenant, TenantSandboxProfileSwitch change, HumanSessionReference human, AuthorizationAudit audit)
    {
        if (audit.AuditId == Guid.Empty || audit.TenantId != tenant || audit.ActorPrincipalId != human.PrincipalId || audit.Action != "sandbox.profile.switch"
            || audit.Target != change.SessionId.ToString("D") || audit.Decision != "allowed" || audit.ReasonCode != "human_profile_switch") throw new ArgumentException("Invalid profile switch audit.");
    }
    private static bool ValidHost(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    public static void ValidateSelection(SandboxLease lease)
    {
        if (lease.SelectionWitness is not { } s) return;
        if (lease.ActiveExecutionId is not null || lease.State is not (SandboxLeaseState.Terminating or SandboxLeaseState.NeedsReconciliation or SandboxLeaseState.Terminated)
            || lease.TransitionPhase != (lease.State == SandboxLeaseState.Terminated ? "awaitingReview" : "retirement")
            || s.RequestId == Guid.Empty || s.InstanceRevision < 1 || !ValidHost(s.HostScope) || !TenantSandboxProfileRules.ValidId(lease.TransitionProfileId)
            || lease.SelectedProfileId != lease.TransitionProfileId || lease.TransitionProfileRevision is not > 0
            || (s.RegistryCredentialId is null ? s.RegistryCredentialRevision is not null || s.RegistrySecretRevision is not null
                : s.RegistryCredentialId == Guid.Empty || s.RegistryCredentialRevision is not > 0 || s.RegistrySecretRevision is not >= 0)) throw new ArgumentException("Invalid selected Sandbox target witness.");
    }
    public static void RequireCurrentSelection(SandboxLease? lease, TenantAgent agent, SandboxProfile? latest)
    {
        if (lease?.SelectionWitness is not { } witness) return;
        if (agent.Instance.Revision != witness.InstanceRevision || latest is null || latest.Id != lease.TransitionProfileId
            || latest.Revision != lease.TransitionProfileRevision || !latest.Enabled) throw new SecurityConflictException("sandbox_selection_changed");
    }
    public static void RequireSelectedProfile(SandboxLease lease, SandboxProfile profile, string? hostScope = null)
    {
        if (lease.SelectionWitness is not { } s) return;
        if (lease.TransitionProfileId != profile.Id || lease.TransitionProfileRevision != profile.Revision || s.InstanceRevision != profile.InstanceRevision
            || s.RegistryCredentialId != profile.RegistryCredentialId || s.RegistryCredentialRevision != profile.RegistryCredentialRevision || s.RegistrySecretRevision != profile.RegistrySecretRevision
            || hostScope is not null && s.HostScope != hostScope) throw new SecurityConflictException("sandbox_selection_changed");
    }
}
