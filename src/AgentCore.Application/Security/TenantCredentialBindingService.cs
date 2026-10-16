using AgentCore.Application.Admin;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantCredentialBindingPage(IReadOnlyList<CredentialBindingView> Items, int Total, int Page, int PageSize,
    bool AllRead, long InstanceRevision, long SecurityRevision);
public sealed record TenantCredentialBindingSaved(CredentialBindingView Binding, long InstanceRevision, long SecurityRevision);
public sealed record TenantCredentialBindingRemoved(long InstanceRevision, long SecurityRevision);

public sealed class TenantCredentialBindingService(IIamStore security, ITenantAgentStore agents, ITenantCredentialStore credentials,
    ITenantCredentialBindingStore bindings, IamAuthorizer authorizer, IBuiltInAgentDefinitionStore builtIns, ITenantDefinitionStore definitions,
    IIdGenerator ids, TimeProvider time, AdminLifecycleCoordinator? lifecycleGate = null)
{
    public async ValueTask<TenantCredentialBindingPage> ListAsync(Guid actor, Guid tenant, Guid instance, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        var state = await ParentState(actor, tenant, instance, false, ct).ConfigureAwait(false);
        var snapshot = await Snapshot(tenant, instance, ct).ConfigureAwait(false);
        var visible = await authorizer.VisibleResourcesAsync(state, actor, "credentials.read", snapshot.Bindings,
            item => TenantCredentialRules.Scope(tenant, item.Binding.CredentialId), ct).ConfigureAwait(false);
        page = Math.Clamp(page, 0, 100_000); pageSize = Math.Clamp(pageSize, 1, 100);
        var items = visible.Items.OrderBy(item => item.Binding.Reference, StringComparer.Ordinal).ToArray();
        return new(items.Skip(page * pageSize).Take(pageSize).Select(View).ToArray(), items.Length, page, pageSize, visible.All,
            snapshot.Agent.Instance.Revision, state.Tenant.Revision);
    }
    public ValueTask<TenantCredentialBindingSaved> BindAsync(Guid actor, Guid tenant, Guid instance, Guid credentialId, string reference,
        long expectedSecurityRevision, long expectedInstanceRevision, long expectedCredentialRevision, CancellationToken ct = default) =>
        WithGate(instance, async token =>
        {
            var state = await ParentState(actor, tenant, instance, true, token).ConfigureAwait(false);
            var parent = await agents.FindTenantAgentAsync(tenant, instance, token).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            var credential = await credentials.FindTenantCredentialAsync(tenant, credentialId, token).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            await RequireCredential(state, actor, credentialId, true, token).ConfigureAwait(false);
            RequireActive(parent.Instance);
            if (state.Tenant.Revision != expectedSecurityRevision || parent.Instance.Revision != expectedInstanceRevision || credential.Value.Revision != expectedCredentialRevision)
                throw new SecurityConflictException("stale_revision");
            await RequireAgentPolicy(state, actor, parent, token).ConfigureAwait(false);
            TenantCredentialBindingRules.Bindable(credential.Value);
            string alias; try { alias = CredentialRules.Reference(reference); } catch (ArgumentException ex) { throw AgentCoreErrors.Validation(ex.Message); }
            var now = time.GetUtcNow(); var binding = new AgentCredentialBinding(ids.NewId(), instance, credentialId, alias, 1, now, now);
            var updated = await bindings.TryBindTenantCredentialAsync(Advance(state), expectedSecurityRevision, binding, expectedInstanceRevision, expectedCredentialRevision,
                Audit(actor, tenant, binding, "bind", now), token).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
            return new TenantCredentialBindingSaved(View(new(binding, credential with { BindingCount = credential.BindingCount + 1 })), updated.Instance.Revision, state.Tenant.Revision + 1);
        }, ct);
    public ValueTask<TenantCredentialBindingRemoved> UnbindAsync(Guid actor, Guid tenant, Guid instance, Guid bindingId,
        long expectedSecurityRevision, long expectedInstanceRevision, long expectedBindingRevision, CancellationToken ct = default) =>
        WithGate(instance, async token =>
        {
            var state = await ParentState(actor, tenant, instance, true, token).ConfigureAwait(false);
            var snapshot = await Snapshot(tenant, instance, token).ConfigureAwait(false);
            var binding = snapshot.Bindings.SingleOrDefault(item => item.Binding.BindingId == bindingId)?.Binding ?? throw new AccessDeniedException("resource_unavailable");
            await RequireCredential(state, actor, binding.CredentialId, false, token).ConfigureAwait(false);
            RequireActive(snapshot.Agent.Instance);
            if (state.Tenant.Revision != expectedSecurityRevision || snapshot.Agent.Instance.Revision != expectedInstanceRevision || binding.Revision != expectedBindingRevision)
                throw new SecurityConflictException("stale_revision");
            await RequireAgentPolicy(state, actor, snapshot.Agent, token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var updated = await bindings.TryUnbindTenantCredentialAsync(Advance(state), expectedSecurityRevision, instance, bindingId, expectedInstanceRevision, expectedBindingRevision,
                now, Audit(actor, tenant, binding, "unbind", now), token).ConfigureAwait(false) ?? throw new SecurityConflictException("stale_revision");
            return new TenantCredentialBindingRemoved(updated.Instance.Revision, state.Tenant.Revision + 1);
        }, ct);
    private async ValueTask<TenantSecurityState> ParentState(Guid actor, Guid tenant, Guid instance, bool write, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        foreach (var key in write ? new[] { "agents.read", "agents.update" } : new[] { "agents.read" })
        {
            var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), TenantAgentServiceScope(tenant, instance), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
        return state;
    }
    private async ValueTask RequireCredential(TenantSecurityState state, Guid actor, Guid id, bool read, CancellationToken ct)
    {
        foreach (var key in read ? new[] { "credentials.bind", "credentials.read" } : new[] { "credentials.bind" })
        {
            var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), TenantCredentialRules.Scope(state.Tenant.TenantId, id), ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode);
        }
    }
    private async ValueTask RequireAgentPolicy(TenantSecurityState state, Guid actor, TenantAgent parent, CancellationToken ct)
    {
        if (state.Principals.Single(p => p.PrincipalId == actor).Kind != PrincipalKind.SystemIdentity) return;
        var instance = parent.Instance;
        var definition = Guid.TryParse(instance.DefinitionId, out var id)
            ? (await definitions.FindDefinitionPublicationAsync(parent.TenantId, id, instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : await builtIns.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct).ConfigureAwait(false);
        if (definition is null) throw new AccessDeniedException("definition_unavailable");
        if (!ManagementPolicyResolver.Resolve(definition, instance).Allows(HarnessManagementScope.Connections))
            throw new AccessDeniedException("management_scope_denied");
    }
    private async ValueTask<TenantCredentialBindingSnapshot> Snapshot(Guid tenant, Guid instance, CancellationToken ct) =>
        await bindings.ReadTenantCredentialBindingsAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
    private ValueTask<T> WithGate<T>(Guid instance, Func<CancellationToken, ValueTask<T>> action, CancellationToken ct) =>
        lifecycleGate is null ? action(ct) : lifecycleGate.WithInstanceAsync(instance, action, ct);
    private static void RequireActive(AgentInstance instance) { if (instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived"); }
    private static AccessScope TenantAgentServiceScope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
    private AuthorizationAudit Audit(Guid actor, Guid tenant, AgentCredentialBinding binding, string action, DateTimeOffset at) =>
        new(ids.NewId(), tenant, actor, "credentials." + action, $"agent:{binding.AgentInstanceId:D}/credential:{binding.CredentialId:D}/binding:{binding.BindingId:D}", "allowed", "binding_changed", at);
    private static CredentialBindingView View(TenantCredentialBinding item) => new(item.Binding.BindingId, item.Binding.CredentialId, item.Binding.Reference,
        item.Binding.Revision, CredentialService.View(item.Credential.Value, item.Credential.BindingCount));
}
