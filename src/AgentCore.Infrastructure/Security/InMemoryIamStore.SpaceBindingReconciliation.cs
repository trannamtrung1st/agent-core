using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Domain.Credentials;
using AgentCore.Application.Security;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private void RequireSpaceCredentialAlias(Guid tenant, AgentCredentialBinding binding)
    {
        var id = binding.CredentialId.ToString("D");
        if (_spaceMemberships.Where(p => p.Key.Tenant == tenant && p.Key.Instance == binding.AgentInstanceId).SelectMany(p => p.Value.AddedBindings)
            .Any(b => b.Kind == SpaceBindingKind.Credential && (b.AccountRef == binding.Reference && b.ResourceKey != id
                || b.ResourceKey == id && b.AccountRef != binding.Reference))) throw new SecurityConflictException("contextual_binding_conflict");
    }

    // Prepare every checked revision before changing either baseline or membership.
    private Action PrepareSpaceExclusionPrune(Guid tenant, Guid instance, IReadOnlySet<string> removedKeys, AuthorizationAudit cause)
    {
        var changes = _spaceMemberships.Where(p => p.Key.Tenant == tenant && p.Key.Instance == instance
                && p.Value.DisabledInheritedBindingKeys.Any(removedKeys.Contains))
            .Select(p => (p.Key, Member: (p.Value with { Revision = checked(p.Value.Revision + 1), UpdatedAt = cause.At,
                DisabledInheritedBindingKeys = p.Value.DisabledInheritedBindingKeys.Where(k => !removedKeys.Contains(k)).ToArray() }).Freeze(),
                Space: _spaces[(tenant, p.Key.Space)] with { Revision = checked(_spaces[(tenant, p.Key.Space)].Revision + 1), UpdatedAt = cause.At },
                Audit: SpaceReconciliationAudit(cause, p.Key.Space, instance))).ToArray();
        return () => {
            foreach (var change in changes)
            {
                _spaceMemberships[change.Key] = change.Member;
                _spaces[(tenant, change.Key.Space)] = change.Space;
                _audit.Add(change.Audit);
            }
        };
    }
    private Action PrepareResourceSpaceReconciliation(Guid tenant, TenantAgentConfigurationChange change, AuthorizationAudit cause, CancellationToken ct)
    {
        var removed = TenantSpaceBindingReconciliation.RemovedResourceKeys(change,
            change.ActiveVersion is null ? null : ChildCatalog(tenant, change.InstanceId, null, ct),
            change.ActiveVersion is null ? null : ChildCatalog(tenant, change.InstanceId, change, ct));
        return PrepareSpaceExclusionPrune(tenant, change.InstanceId, removed, cause);
    }
    private static AuthorizationAudit SpaceReconciliationAudit(AuthorizationAudit cause, Guid space, Guid instance)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"AgentCore.SpaceReconciliation\0{cause.AuditId:D}\0{space:D}\0{instance:D}"));
        return new(new Guid(hash.AsSpan(0, 16)), cause.TenantId, cause.ActorPrincipalId, "space-memberships.reconcile", space.ToString("D"),
            "allowed", "inherited_binding_removed", cause.At);
    }
}
