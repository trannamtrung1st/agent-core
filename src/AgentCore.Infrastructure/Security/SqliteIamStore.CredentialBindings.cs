using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantCredentialBindingSnapshot?> ReadTenantCredentialBindingsAsync(Guid tenantId, Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var parent = await OwnedAgents(db, tenantId).SingleOrDefaultAsync(item => item.Instance.InstanceId == instanceId.ToString("D"), ct).ConfigureAwait(false);
        if (parent is null) return null;
        var rows = await OwnedCredentialBindings(db, tenantId, instanceId).AsNoTracking().OrderBy(b => b.Reference).ToArrayAsync(ct).ConfigureAwait(false);
        var items = new List<TenantCredentialBinding>();
        foreach (var row in rows)
        {
            var value = await OwnedCredentials(db, tenantId).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking()
                .SingleAsync(c => c.CredentialId == row.CredentialId, ct).ConfigureAwait(false);
            if (await CredentialView(db, tenantId, value, ct).ConfigureAwait(false) is { } credential) items.Add(new(MapBinding(row), credential));
        }
        return new(Owned(parent), items);
    }
    public async ValueTask<TenantAgent?> TryBindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, AgentCredentialBinding binding,
        long expectedInstanceRevision, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        next = IamStateRules.Freeze(next); TenantCredentialBindingRules.NewBinding(binding);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, binding.AgentInstanceId, AgentInstanceLifecycle.Active, audit);
        TenantAgent? result = null;
        var saved = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            // Inventory joins default to no tracking; this transaction must update the backing Instance.
            var parent = await OwnedAgents(db, next.Tenant.TenantId).AsTracking().SingleOrDefaultAsync(item => item.Instance.InstanceId == binding.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
            if (parent is null || parent.Instance.Revision != expectedInstanceRevision) return false;
            if (parent.Instance.Lifecycle != nameof(AgentInstanceLifecycle.Active)) throw new AccessDeniedException("agent_archived");
            if (!await StageTenantCredentialBinding(db, next.Tenant.TenantId, binding, false, expectedCredentialRevision, audit, ct).ConfigureAwait(false)) return false;
            parent.Instance.Revision++; parent.Instance.UpdatedAtUtc = binding.UpdatedAtUtc.ToUnixTimeMilliseconds(); result = Owned(parent); return true;
        }, ct, initiator).ConfigureAwait(false);
        return saved ? result : null;
    }
    public async ValueTask<TenantAgent?> TryUnbindTenantCredentialAsync(TenantSecurityState next, long expectedSecurityRevision, Guid instanceId,
        Guid bindingId, long expectedInstanceRevision, long expectedBindingRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
    {
        next = IamStateRules.Freeze(next);
        IamStateRules.ValidateAgentWrite(next, expectedSecurityRevision, instanceId, AgentInstanceLifecycle.Active, audit);
        TenantAgent? result = null;
        var saved = await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var parent = await OwnedAgents(db, next.Tenant.TenantId).AsTracking().SingleOrDefaultAsync(item => item.Instance.InstanceId == instanceId.ToString("D"), ct).ConfigureAwait(false);
            if (parent is null || parent.Instance.Revision != expectedInstanceRevision) return false;
            if (parent.Instance.Lifecycle != nameof(AgentInstanceLifecycle.Active)) throw new AccessDeniedException("agent_archived");
            var binding = await OwnedCredentialBindings(db, next.Tenant.TenantId, instanceId).SingleOrDefaultAsync(b => b.BindingId == bindingId.ToString("D"), ct).ConfigureAwait(false);
            if (binding is null || binding.Revision != expectedBindingRevision) return false;
            if (!await StageTenantCredentialBinding(db, next.Tenant.TenantId, MapBinding(binding), true, 0, audit, ct).ConfigureAwait(false)) return false;
            parent.Instance.Revision++; parent.Instance.UpdatedAtUtc = at.ToUnixTimeMilliseconds();
            result = Owned(parent); return true;
        }, ct, initiator).ConfigureAwait(false);
        return saved ? result : null;
    }
    // Human binding commands and model exact-operation commits share this association mutation.
    private async Task<bool> StageTenantCredentialBinding(AgentCoreDbContext db, Guid tenant, AgentCredentialBinding value,
        bool remove, long expectedCredentialRevision, AuthorizationAudit audit, CancellationToken ct)
    {
        if (remove)
        {
            var row = await OwnedCredentialBindings(db, tenant, value.AgentInstanceId).SingleOrDefaultAsync(b => b.BindingId == value.BindingId.ToString("D"), ct).ConfigureAwait(false);
            if (row is null || row.Revision != value.Revision || row.CredentialId != value.CredentialId.ToString("D") || row.Reference != value.Reference) return false;
            await PruneSpaceExclusion(db, tenant, value.AgentInstanceId, new HashSet<string>(StringComparer.Ordinal) { "Credential:" + value.CredentialId.ToString("D") }, audit, ct).ConfigureAwait(false);
            db.AgentCredentialBindings.Remove(row); return true;
        }
        TenantCredentialBindingRules.NewBinding(value);
        var instanceKey = value.AgentInstanceId.ToString("D"); var credentialKey = value.CredentialId.ToString("D");
        if (await db.Set<SpaceAddedBindingRecord>().AnyAsync(b => b.TenantId == tenant && b.InstanceId == instanceKey
            && b.Kind == AgentCore.Domain.Spaces.SpaceBindingKind.Credential && (b.AccountRef == value.Reference && b.ResourceKey != credentialKey
                || b.ResourceKey == credentialKey && b.AccountRef != value.Reference), ct).ConfigureAwait(false))
            throw new SecurityConflictException("contextual_binding_conflict");
        var credential = await OwnedCredentials(db, tenant).Include(c => c.EmailAccount).Include(c => c.DriveAccount)
            .SingleOrDefaultAsync(c => c.CredentialId == value.CredentialId.ToString("D"), ct).ConfigureAwait(false);
        if (credential is null || credential.Revision != expectedCredentialRevision) return false;
        TenantCredentialBindingRules.Bindable(SqliteCredentialStore.Map(credential));
        if (await db.AgentCredentialBindings.AnyAsync(b => b.BindingId == value.BindingId.ToString("D") || b.AgentInstanceId == value.AgentInstanceId.ToString("D")
            && (b.Reference == value.Reference || b.CredentialId == value.CredentialId.ToString("D")), ct).ConfigureAwait(false)) throw new SecurityConflictException("credential_binding_conflict");
        db.AgentCredentialBindings.Add(new() { TenantId = tenant, BindingId = value.BindingId.ToString("D"), AgentInstanceId = value.AgentInstanceId.ToString("D"),
            CredentialId = value.CredentialId.ToString("D"), Reference = value.Reference, Revision = value.Revision,
            CreatedAtUtc = value.CreatedAtUtc.ToUnixTimeMilliseconds(), UpdatedAtUtc = value.UpdatedAtUtc.ToUnixTimeMilliseconds() });
        return true;
    }
    private static IQueryable<AgentCredentialBindingRecord> OwnedCredentialBindings(AgentCoreDbContext db, Guid tenant, Guid instance) => db.AgentCredentialBindings
        .Where(b => b.TenantId == tenant && b.AgentInstanceId == instance.ToString("D")
            && db.Set<IamSystemIdentityRow>().Any(i => i.TenantId == tenant && i.AgentInstanceId == b.AgentInstanceId)
            && db.Credentials.Any(c => c.CredentialId == b.CredentialId && db.Set<TenantCredentialRecord>()
                .Any(r => r.TenantId == tenant && r.CredentialId == c.CredentialId && r.DeletedAt == null)));
    private static AgentCredentialBinding MapBinding(AgentCredentialBindingRecord row) => new(Guid.Parse(row.BindingId), Guid.Parse(row.AgentInstanceId), Guid.Parse(row.CredentialId),
        row.Reference, row.Revision, DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));
}
