using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantSandboxControlProfile(string Id, long Revision, string Name, string ImageReference, string Availability, bool CanSelect);
public sealed record TenantSandboxControlsView(TenantSandboxRuntimeView Observation, long InstanceRevision, bool CanReset, bool CanStop,
    IReadOnlyList<TenantSandboxControlProfile> Profiles, string? DefaultProfileId);

public sealed partial class TenantSandboxRuntimeReadService
{
    // Human UI preflight only. A hint never replaces the existing exact command transaction.
    public async ValueTask<TenantSandboxControlsView> ReadControlsAsync(Guid tenant, Guid session, HumanSessionReference human, CancellationToken ct = default)
    {
        var before = await ReadHuman(tenant, session, human, null, ct).ConfigureAwait(false);
        if (runConfiguration is null || profileStore is null || images is null) throw new AccessDeniedException("tenant_effect_unavailable");
        var (agent, policy) = await runConfiguration.CurrentPolicyAsync(tenant, before.Session.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (agent.PrincipalId != before.Session.Owner.ExecutorPrincipalId) throw new AccessDeniedException("resource_unavailable");
        var lease = before.Snapshot.Lease; var execution = before.Snapshot.Execution;
        var manageable = await CanControl(before.Security, before.Session.Owner, human, lease?.Profile.Id, ct).ConfigureAwait(false);
        var reset = lease is null ? null : new TenantSandboxReset(session, before.Snapshot.SessionRevision, lease.Generation, lease.Revision, lease.ActiveExecutionId ?? lease.LastExecutionId);
        var canReset = manageable && reset is not null && Available(() => TenantSandboxControlRules.Require(reset, before.Session, lease!));
        var canStop = canReset && execution is not null && Available(() => TenantSandboxControlRules.Require(
            new TenantSandboxProcessStop(session, before.Snapshot.SessionRevision, lease!.Generation, lease.Revision, execution.Id, execution.Revision), before.Session, lease, execution));
        var profiles = new List<TenantSandboxControlProfile>();
        foreach (var id in (policy.SandboxPolicy?.AllowedProfileIds ?? []).Take(16))
        {
            if (!(await authorizer.CheckTenantAsync(before.Security, human.PrincipalId, PermissionCatalog.Get("sandbox.read"),
                TenantSandboxProfileRules.Scope(tenant, id), ct).ConfigureAwait(false)).Allowed) continue;
            var source = await profileStore.FindTenantSandboxProfileAsync(tenant, id, ct: ct).ConfigureAwait(false);
            if (source is null) continue; // No global source or inaccessible/default ID fallback.
            var availability = await images.InspectAsync(source, ct).ConfigureAwait(false);
            var ready = readiness?.State == "Ready" && host is not null;
            var canSelect = false;
            if (canReset && ready && availability.Eligible && RolePermissions.AllowsTool(policy, ToolCatalog.SandboxExec))
                canSelect = Available(() => {
                    var target = SandboxSetupResolver.Resolve(source, agent.Instance.SettingsOverrides, agent.Instance.Revision, rejectExpansion: true)
                        with { RegistryCredentialRevision = availability.CredentialRevision, RegistrySecretRevision = availability.SecretRevision };
                    var change = new TenantSandboxProfileSwitch(session, before.Snapshot.SessionRevision, lease!.Generation, lease.Revision,
                        lease.ActiveExecutionId ?? lease.LastExecutionId, id, source.Revision, agent.Instance.Revision);
                    TenantSandboxControlRules.RequireSelection(new(change, target, host!.Scope), before.Session, lease, execution, agent);
                });
            profiles.Add(new(id, source.Revision, source.Name, source.ImageReference,
                availability.Eligible && !ready ? "ServerUnavailable" : availability.Status, canSelect));
        }
        var (currentAgent, _) = await runConfiguration.CurrentPolicyAsync(tenant, before.Session.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        var after = await ReadHuman(tenant, session, human, null, ct).ConfigureAwait(false);
        if (after.Security.Tenant.Revision != before.Security.Tenant.Revision || after.Session.Owner != before.Session.Owner
            || after.View.SessionRevision != before.View.SessionRevision || after.View.Environment?.Generation != before.View.Environment?.Generation
            || after.View.Environment?.Revision != before.View.Environment?.Revision || after.View.Execution?.ExecutionId != before.View.Execution?.ExecutionId
            || after.View.Execution?.Revision != before.View.Execution?.Revision || currentAgent.Instance.Revision != agent.Instance.Revision
            || currentAgent.PrincipalId != agent.PrincipalId) throw new SecurityConflictException("stale_revision");
        var defaultId = policy.SandboxPolicy?.DefaultProfileId;
        return new(after.View, agent.Instance.Revision, canReset, canStop, profiles,
            profiles.Any(p => p.Id == defaultId) ? defaultId : null);
    }

    private async ValueTask<bool> CanControl(TenantSecurityState state, TenantSessionRoot owner, HumanSessionReference human, string? profile, CancellationToken ct)
    {
        try { await TenantSandboxControlRules.RequirePermissionsAsync(state, owner, profile, human, authorizer, ct).ConfigureAwait(false); return true; }
        catch (AccessDeniedException) { return false; }
    }
    private static bool Available(Action check)
    {
        try { check(); return true; }
        catch (AccessDeniedException) { return false; }
        catch (SecurityConflictException) { return false; }
        catch (ArgumentException) { return false; }
        catch (AgentCoreException error) when (error.Code == "ValidationError") { return false; }
    }
}
