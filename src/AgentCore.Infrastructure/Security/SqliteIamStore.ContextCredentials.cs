using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<IReadOnlyList<TenantCredentialBinding>> ContextCredentialBindings(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var state = await ReadTenantAsync(db, run.Owner.TenantId!.Value, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        await RequireSpaceExecutionSource(db, run, state, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var pin = run.Admission.Configuration?.SpaceContext ?? throw new AccessDeniedException("space_context_required");
        pin.Validate(); var tenant = run.Owner.TenantId!.Value; var instance = run.AgentInstanceId.ToString("D");
        if (pin.TenantId != tenant) throw new AccessDeniedException("tenant_context_mismatch");
        var space = await db.Set<SpaceRecord>().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SpaceId == pin.SpaceId && s.DeletedAt == null, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_unavailable");
        if (space.Kind == SpaceKind.Private && space.OwnerInstanceId != instance) throw new AccessDeniedException("space_unavailable");
        var row = await db.Set<SpaceMembershipRecord>().SingleOrDefaultAsync(m => m.TenantId == tenant && m.SpaceId == pin.SpaceId && m.InstanceId == instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("space_membership_unavailable");
        if (pin.EnvironmentId is { } environment && (!space.EnvironmentsEnabled || space.EnvironmentSelectionEpoch != pin.EnvironmentSelectionEpoch
            || !await db.Set<SpaceEnvironmentRecord>().AnyAsync(e => e.TenantId == tenant && e.SpaceId == pin.SpaceId && e.EnvironmentId == environment && e.DeletedAt == null, ct).ConfigureAwait(false))) throw new AccessDeniedException("environment_unavailable");
        var disabled = await db.Set<SpaceDisabledBindingRecord>().Where(b => b.TenantId == tenant && b.SpaceId == pin.SpaceId && b.InstanceId == instance).ToArrayAsync(ct).ConfigureAwait(false);
        var added = await db.Set<SpaceAddedBindingRecord>().Where(b => b.TenantId == tenant && b.SpaceId == pin.SpaceId && b.InstanceId == instance).ToArrayAsync(ct).ConfigureAwait(false);
        var member = row.Domain(disabled, added);
        var inherited = (await OwnedCredentialBindings(db, tenant, run.AgentInstanceId).AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false)).Select(MapBinding).ToArray();
        IReadOnlyList<SpaceResourceBinding> effective;
        try { effective = EffectiveSpaceBindings.Resolve(inherited.Select(b => new SpaceResourceBinding(SpaceBindingKind.Credential, b.CredentialId.ToString("D"), b.Reference)).ToArray(), member); }
        catch (ArgumentException) { throw new AccessDeniedException("contextual_binding_ambiguous"); }
        var result = new List<TenantCredentialBinding>();
        foreach (var binding in effective.Where(b => b.Kind == SpaceBindingKind.Credential))
        {
            var id = Guid.Parse(binding.ResourceKey);
            var credentialRow = await OwnedCredentials(db, tenant).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == binding.ResourceKey, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_unavailable");
            var credential = await CredentialView(db, tenant, credentialRow, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_unavailable");
            var association = (member.DisabledInheritedBindingKeys.Contains(binding.StableKey, StringComparer.Ordinal) ? null
                : inherited.SingleOrDefault(b => b.CredentialId == id && b.Reference == binding.AccountRef))
                ?? TenantRunConfigurationResolver.ContextualCredentialBinding(tenant, pin.SpaceId, run.AgentInstanceId, id, binding.AccountRef!, member);
            result.Add(new(association, credential));
        }
        return result;
    }
    private async ValueTask<TenantCredentialBinding> ContextCredentialTarget(AgentCoreDbContext db, AgentRun run, Guid binding, Guid credential, CancellationToken ct) =>
        (await ContextCredentialBindings(db, run, ct).ConfigureAwait(false)).SingleOrDefault(b => b.Binding.BindingId == binding && b.Binding.CredentialId == credential)
        ?? throw new AccessDeniedException("credential_binding_changed");
}
