using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private IReadOnlyList<TenantCredentialBinding> ContextCredentialBindings(AgentRun run, CancellationToken ct = default)
    {
        RequireSpaceExecutionSource(run, _tenants[run.Owner.TenantId!.Value], new IamAuthorizer(this), ct).GetAwaiter().GetResult();
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        pin.Validate(); var tenant = run.Owner.TenantId!.Value;
        if (pin.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        var space = FindSpaceAsync(tenant, pin.SpaceId, ct).Result ?? throw new AccessDeniedException("space_unavailable");
        if (space.Kind == SpaceKind.Private && space.OwnerInstanceId != run.AgentInstanceId) throw new AccessDeniedException("space_unavailable");
        var member = _spaceMemberships.GetValueOrDefault((tenant, pin.SpaceId, run.AgentInstanceId)) ?? throw new AccessDeniedException("space_membership_unavailable");
        if (pin.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != pin.EnvironmentSelectionEpoch
            || FindEnvironmentAsync(tenant, pin.SpaceId, environment, ct).Result is null)) throw new AccessDeniedException("environment_unavailable");
        var inherited = ReadTenantCredentialBindingsAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("executor_unavailable");
        IReadOnlyList<SpaceResourceBinding> effective;
        try { effective = EffectiveSpaceBindings.Resolve(inherited.Bindings.Select(b => new SpaceResourceBinding(SpaceBindingKind.Credential, b.Binding.CredentialId.ToString("D"), b.Binding.Reference)).ToArray(), member); }
        catch (ArgumentException) { throw new AccessDeniedException("contextual_binding_ambiguous"); }
        var result = new List<TenantCredentialBinding>();
        foreach (var binding in effective.Where(b => b.Kind == SpaceBindingKind.Credential))
        {
            var id = Guid.Parse(binding.ResourceKey);
            var credential = CredentialBacking().ReadTenantCredential(tenant, id) ?? throw new AccessDeniedException("credential_unavailable");
            var association = (member.DisabledInheritedBindingKeys.Contains(binding.StableKey, StringComparer.Ordinal) ? null
                : inherited.Bindings.Select(b => b.Binding).SingleOrDefault(b => b.CredentialId == id && b.Reference == binding.AccountRef))
                ?? TenantRunConfigurationResolver.ContextualCredentialBinding(tenant, pin.SpaceId, run.AgentInstanceId, id, binding.AccountRef!, member);
            result.Add(new(association, credential));
        }
        return result;
    }
}
