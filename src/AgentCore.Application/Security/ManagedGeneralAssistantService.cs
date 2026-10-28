using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

// Trusted coordinator, also used by the explicitly authorized setup retry.
public sealed class ManagedGeneralAssistantService(IIamStore security, ITenantAgentStore agents,
    IBuiltInAgentDefinitionStore definitions, IIdGenerator ids, TimeProvider time, Func<TenantAgentConfigurationService> configuration,
    ILogger<ManagedGeneralAssistantService>? logger = null)
{
    // Tenant creation has already committed. Keep its successful result truthful and let
    // startup or an explicit owner retry repair setup through the same bounded operation.
    public async ValueTask<bool> TryReconcileAsync(Guid tenant, CancellationToken ct = default)
    {
        try { await ReconcileAsync(tenant, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var reason = error switch {
                SecurityConflictException conflict => conflict.ReasonCode,
                AccessDeniedException denied => denied.ReasonCode,
                AgentCore.Application.Sessions.AgentCoreException application => application.Code,
                System.Data.Common.DbException => "storage_failure",
                _ => "unexpected_failure"
            };
            // Exception messages/stacks can contain provider bodies, SQL or credentials.
            // Retain safe type/code/correlation evidence without serializing the exception.
            logger?.LogError("General Assistant setup pending for tenant {TenantId}; reason {ReasonCode}; exception {ExceptionType}; error {ErrorCode}; trace {TraceId}. An owner can retry setup in Chat.",
                tenant, reason, error.GetType().FullName, error.HResult, System.Diagnostics.Activity.Current?.Id);
            return false;
        }
    }
    public async ValueTask<TenantAgent> ReconcileAsync(Guid tenant, CancellationToken ct = default)
    {
        var definition = await definitions.GetAsync(ManagedGeneralAssistant.DefinitionId, cancellationToken: ct)
            ?? throw new InvalidOperationException("The built-in General Assistant definition is missing.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var state = await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
            if (state.Tenant.Status != IdentityStatus.Active) throw new AccessDeniedException("tenant_unavailable");
            var existing = state.SystemIdentities.SingleOrDefault(i => i.ManagedKind == ManagedGeneralAssistant.Kind);
            if (existing is not null)
            {
                var agent = await agents.FindTenantAgentAsync(tenant, existing.AgentInstanceId!.Value, ct)
                    ?? throw new InvalidOperationException("The managed assistant lost its backing Instance.");
                ManagedGeneralAssistant.Require(agent);
                if (agent.Instance.ActiveVersion == definition.Version) return agent;
                try { return await configuration().AdoptSystemDefaultsAsync(tenant, agent.Instance.InstanceId, definition, ct); }
                catch (SecurityConflictException error) when (error.ReasonCode == "stale_revision" && attempt < 4) { continue; }
            }
            var now = time.GetUtcNow(); var id = ids.NewId(); var system = ids.NewId(); var principal = ids.NewId();
            var instance = new AgentInstance(id, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, now, now);
            var roles = state.Roles.ToList();
            foreach (var key in new[] { ManagedGeneralAssistant.StarterRole, ManagedGeneralAssistant.FilesRole, ManagedGeneralAssistant.ContextRole })
                if (!roles.Any(r => r.Key == key)) roles.Add(BuiltinAccessRoles.ForTenant(tenant, ids.NewId).Single(r => r.Key == key));
            var next = state with {
                Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) }, Roles = roles,
                Principals = state.Principals.Append(new(principal, PrincipalKind.SystemIdentity, tenant, null, null, system, IdentityStatus.Active)).ToArray(),
                SystemIdentities = state.SystemIdentities.Append(new(system, tenant, principal, SystemIdentityKind.Agent, id, IdentityStatus.Active, ManagedGeneralAssistant.Kind)).ToArray(),
                Assignments = state.Assignments.Append(new(ids.NewId(), tenant, principal, roles.Single(r => r.Key == ManagedGeneralAssistant.StarterRole).RoleId,
                    AccessScope.ForTenant(tenant), Guid.Empty, now)).Append(new(ids.NewId(), tenant, principal, roles.Single(r => r.Key == ManagedGeneralAssistant.ContextRole).RoleId,
                    AccessScope.ForResource(tenant, "agent", id.ToString("D")), Guid.Empty, now)).ToArray()
            };
            if (await agents.TryProvisionGeneralAssistantAsync(next, state.Tenant.Revision, instance, definition.SkillList,
                new(ids.NewId(), tenant, Guid.Empty, ManagedGeneralAssistant.ProvisionAction, id.ToString("D"), "allowed", "system_owned_default", now), ct))
                return await agents.FindTenantAgentAsync(tenant, id, ct) ?? throw new InvalidOperationException("Managed provisioning readback failed.");
        }
        throw new SecurityConflictException("stale_revision");
    }
}
