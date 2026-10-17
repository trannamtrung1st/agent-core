using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private static readonly JsonSerializerOptions SandboxJson = new(JsonSerializerDefaults.Web);
    private static IQueryable<SandboxProfileRow> OwnedSandboxProfiles(AgentCoreDbContext db, Guid tenant) => db.Set<SandboxProfileRow>().Where(p => p.TenantId == tenant
        && db.Set<TenantSandboxProfileRecord>().Any(r => r.TenantId == tenant && r.ProfileId == p.Id));
    private static SandboxProfile SandboxValue(SandboxProfileRow row)
    {
        var value = JsonSerializer.Deserialize<SandboxProfile>(row.Json, SandboxJson);
        if (value is null || value.TenantId != row.TenantId || value.Id != row.Id || value.Revision != row.Revision)
            throw AgentCore.Application.Sessions.AgentCoreErrors.Persistence("Sandbox profile ownership is invalid.");
        return value;
    }
    public async ValueTask<SandboxProfile?> FindTenantSandboxProfileAsync(Guid tenant, string id, long? revision = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await OwnedSandboxProfiles(db, tenant).AsNoTracking().Where(r => r.Id == id && (revision == null || r.Revision == revision))
            .OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return row is null ? null : SandboxValue(row);
    }
    public async ValueTask<TenantSandboxProfilePage> ListTenantSandboxProfilesAsync(Guid tenant, IReadOnlyList<string>? allowedIds,
        int page, int pageSize, string search, CancellationToken ct = default)
    {
        TenantSandboxProfileRules.Query(tenant, page, pageSize, search);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = OwnedSandboxProfiles(db, tenant).AsNoTracking();
        if (allowedIds is not null) query = query.Where(r => allowedIds.Contains(r.Id));
        query = query.Where(r => !db.Set<SandboxProfileRow>().Any(n => n.TenantId == tenant && n.Id == r.Id && n.Revision > r.Revision));
        var term = TenantSandboxProfileRules.SearchTerm(search);
        if (term.Length > 0) query = query.Where(r => r.SearchText.Contains(term));
        var count = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query.OrderBy(r => r.Id).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Select(SandboxValue).ToArray(), count, page, pageSize);
    }
    public async ValueTask<bool> TrySaveTenantSandboxProfileAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantSandboxProfileRoot root, SandboxProfile profile, long expectedProfileRevision,
        HumanSessionReference human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); TenantSandboxProfileRules.Validate(root, profile, expectedProfileRevision);
        if (root.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != human.PrincipalId) throw new ArgumentException("Invalid Sandbox profile transaction.");
        return await WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var owner = await db.Set<TenantSandboxProfileRecord>().SingleOrDefaultAsync(r => r.ProfileId == root.ProfileId, ct).ConfigureAwait(false);
            var old = await db.Set<SandboxProfileRow>().Where(r => r.Id == root.ProfileId).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (owner is null ? expectedProfileRevision != 0 || old is not null : owner.TenantId != root.TenantId || old?.TenantId != root.TenantId) return false;
            if ((old?.Revision ?? 0) != expectedProfileRevision) return false;
            if (!TenantSandboxProfileRules.DisablingUnchangedSource(old is null ? null : SandboxValue(old), profile) && profile.RegistryCredentialId is { } credential)
            {
                var row = await OwnedCredentials(db, root.TenantId).SingleOrDefaultAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
                TenantSandboxProfileRules.RequireCredential(profile, row is null ? null : SqliteCredentialStore.Map(row), (clock ?? TimeProvider.System).GetUtcNow());
            }
            await RequireAgentHumanAsync(db, root.TenantId, audit.ActorPrincipalId, human, ct).ConfigureAwait(false);
            if (owner is null) db.Add(new TenantSandboxProfileRecord(root.ProfileId, root.TenantId, root.CreatedAt));
            db.Add(new SandboxProfileRow { TenantId = root.TenantId, Id = profile.Id, Revision = profile.Revision, SearchText = TenantSandboxProfileRules.SearchText(profile), Json = JsonSerializer.Serialize(profile, SandboxJson) });
            return true;
        }, ct).ConfigureAwait(false);
    }
}
