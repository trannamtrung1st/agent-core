using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantAgentService(IIamStore security, ITenantAgentStore agents, IBuiltInAgentDefinitionStore templates,
    IamAuthorizer authorizer, IIdGenerator ids, TimeProvider time, AdminLifecycleCoordinator? lifecycleGate = null,
    Triggers.ITriggerInstancePolicyReconciliationService? reconciliation = null, SandboxPolicyEnforcement? sandbox = null, ITenantDefinitionStore? ownedDefinitions = null)
{
    public async ValueTask<IReadOnlyList<AgentDefinition>> ListTemplatesAsync(Guid actorId, Guid tenantId, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actorId, "agents.create", AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        return await templates.ListAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantAgent> CreateAsync(Guid actorId, Guid tenantId, string templateId, int version,
        long expectedRevision, AgentIdentity? persona = null, CancellationToken ct = default)
    {
        var state = await ReadForChangeAsync(actorId, tenantId, "agents.create", AccessScope.ForTenant(tenantId), expectedRevision, ct).ConfigureAwait(false);
        if (version < 1 || string.IsNullOrWhiteSpace(templateId)) throw new ArgumentException("An exact template version is required.");
        // Only immutable shipped templates are globally readable. Tenant publications must use their
        // own converted ownership store; the old composite/global catalog is never a fallback here.
        var template = await templates.GetAsync(templateId, version, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("definition_unavailable");
        if (version < 1 || template.Version != version) throw new ArgumentException("An exact template version is required.");
        return await CreateOwnedAsync(actorId, tenantId, expectedRevision, state, template, persona, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantAgent> CreateFromPublicationAsync(Guid actorId, Guid tenantId, Guid definitionId, int version,
        long expectedRevision, AgentIdentity? persona = null, CancellationToken ct = default)
    {
        var state = await ReadForChangeAsync(actorId, tenantId, "agents.create", AccessScope.ForTenant(tenantId), expectedRevision, ct).ConfigureAwait(false);
        await authorizer.RequireAsync(actorId, "agent-definitions.read", AccessScope.ForResource(tenantId, "definition", definitionId.ToString("D")), ct).ConfigureAwait(false);
        var publication = ownedDefinitions is null ? null : await ownedDefinitions.FindDefinitionPublicationAsync(tenantId, definitionId, version, ct).ConfigureAwait(false);
        if (publication?.Status != DefinitionPublicationStatus.Active) throw new AccessDeniedException("definition_unavailable");
        return await CreateOwnedAsync(actorId, tenantId, expectedRevision, state, publication.Payload, persona, ct).ConfigureAwait(false);
    }

    private async ValueTask<TenantAgent> CreateOwnedAsync(Guid actorId, Guid tenantId, long expectedRevision,
        TenantSecurityState state, AgentDefinition template, AgentIdentity? persona, CancellationToken ct)
    {
        var identity = persona ?? template.Identity;
        AgentDefinitionValidator.ValidateIdentity(identity);
        var now = time.GetUtcNow(); var instanceId = ids.NewId(); var systemId = ids.NewId(); var principalId = ids.NewId();
        var instance = new AgentInstance(instanceId, template.Id, template.Version, identity, AgentInstanceLifecycle.Active, now, now);
        var next = Advance(state with
        {
            Principals = state.Principals.Append(new(principalId, PrincipalKind.SystemIdentity, tenantId, null, null, systemId, IdentityStatus.Active)).ToArray(),
            SystemIdentities = state.SystemIdentities.Append(new(systemId, tenantId, principalId, SystemIdentityKind.Agent, instanceId, IdentityStatus.Active)).ToArray()
        });
        if (!await agents.TryCreateTenantAgentAsync(next, expectedRevision, instance, template.SkillList,
            Audit(tenantId, actorId, "agents.create", instanceId, now), ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return new(tenantId, systemId, principalId, instance);
    }

    public async ValueTask<TenantAgent> ReadAsync(Guid actorId, Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actorId, "agents.read", Scope(tenantId, instanceId), ct).ConfigureAwait(false);
        return await agents.FindTenantAgentAsync(tenantId, instanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    }

    public async ValueTask<TenantAgentPage> ListAsync(Guid actorId, Guid tenantId, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        var state = await security.ReadTenantAsync(tenantId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var all = await authorizer.CheckTenantAsync(state, actorId, PermissionCatalog.Get("agents.read"), AccessScope.ForTenant(tenantId), ct).ConfigureAwait(false);
        List<Guid>? allowed = null;
        if (!all.Allowed)
        {
            if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
            allowed = [];
            foreach (var identity in state.SystemIdentities)
                if (identity.AgentInstanceId is { } instanceId && (await authorizer.CheckTenantAsync(state, actorId,
                    PermissionCatalog.Get("agents.read"), Scope(tenantId, instanceId), ct).ConfigureAwait(false)).Allowed) allowed.Add(instanceId);
        }
        return await agents.ListTenantAgentsAsync(tenantId, allowed, Math.Clamp(page, 0, 100_000), Math.Clamp(pageSize, 1, 100), ct).ConfigureAwait(false);
    }

    public ValueTask<TenantAgent> SetLifecycleAsync(Guid actorId, Guid tenantId, Guid instanceId, AgentInstanceLifecycle lifecycle,
        long expectedRevision, long expectedInstanceRevision, CancellationToken ct = default) => lifecycleGate is null
        ? ChangeLifecycleAsync(actorId, tenantId, instanceId, lifecycle, expectedRevision, expectedInstanceRevision, ct)
        : lifecycleGate.WithInstanceAsync(instanceId, token => ChangeLifecycleAsync(actorId, tenantId, instanceId, lifecycle,
            expectedRevision, expectedInstanceRevision, token), ct);

    private async ValueTask<TenantAgent> ChangeLifecycleAsync(Guid actorId, Guid tenantId, Guid instanceId, AgentInstanceLifecycle lifecycle,
        long expectedRevision, long expectedInstanceRevision, CancellationToken ct)
    {
        if (!Enum.IsDefined(lifecycle)) throw new ArgumentException("Unknown agent lifecycle.");
        var state = await ReadForChangeAsync(actorId, tenantId, "agents.update", Scope(tenantId, instanceId), expectedRevision, ct).ConfigureAwait(false);
        var owned = state.SystemIdentities.Single(i => i.AgentInstanceId == instanceId);
        var status = lifecycle == AgentInstanceLifecycle.Active ? IdentityStatus.Active : IdentityStatus.Suspended;
        var next = Advance(state with
        {
            SystemIdentities = state.SystemIdentities.Select(i => i.SystemIdentityId == owned.SystemIdentityId ? i with { Status = status } : i).ToArray(),
            Principals = state.Principals.Select(p => p.PrincipalId == owned.PrincipalId ? p with { Status = status } : p).ToArray()
        });
        var now = time.GetUtcNow();
        var updated = await agents.TrySetTenantAgentLifecycleAsync(next, expectedRevision, instanceId, expectedInstanceRevision, lifecycle,
            now, Audit(tenantId, actorId, lifecycle == AgentInstanceLifecycle.Active ? "agents.restore" : "agents.archive", instanceId, now), ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        if (reconciliation is not null) await reconciliation.ReconcileAgentInstanceAsync(instanceId, now, ct).ConfigureAwait(false);
        if (sandbox is not null) await sandbox.EnforceAsync(instanceId, ct).ConfigureAwait(false);
        return updated;
    }

    private async ValueTask<TenantSecurityState> ReadForChangeAsync(Guid actor, Guid tenant, string permission, AccessScope scope, long expected, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false);
        if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision");
        return state;
    }
    private static AccessScope Scope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(Guid tenant, Guid actor, string action, Guid target, DateTimeOffset at) => new(ids.NewId(), tenant, actor, action, target.ToString("D"), "allowed", "authorized", at);
}
