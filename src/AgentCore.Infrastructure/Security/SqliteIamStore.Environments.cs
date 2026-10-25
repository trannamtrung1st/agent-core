using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<bool> IsSecretCreationActorAsync(Guid tenant, Guid secret, Guid actor, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); var target=secret.ToString("D");
        return await db.Set<AuthorizationAudit>().AnyAsync(a => a.TenantId == tenant && a.Action == "secrets.create" && a.Target == target && a.ActorPrincipalId == actor && a.Decision == "allowed", ct).ConfigureAwait(false);
    }
    public async ValueTask<Secret?> FindSecretAsync(Guid tenant, Guid secret, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Set<Secret>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SecretId == secret && s.Status != SecretStatus.Deleted, ct).ConfigureAwait(false);
    }
    public async ValueTask<SecretPage> ListSecretsAsync(Guid tenant, IReadOnlyList<Guid>? allowedSecrets, int page, int pageSize, CancellationToken ct = default)
    {
        SpaceStoreRules.Page(tenant, page, pageSize); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Set<Secret>().AsNoTracking().Where(s => s.TenantId == tenant && s.Status != SecretStatus.Deleted);
        if (allowedSecrets is not null) query = query.Where(s => allowedSecrets.Contains(s.SecretId));
        var total = await query.CountAsync(ct).ConfigureAwait(false);
        return new(await query.OrderBy(s => s.SecretId).Skip(page * pageSize).Take(pageSize).ToArrayAsync(ct).ConfigureAwait(false), total, page, pageSize);
    }
    public ValueTask<bool> TrySaveSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Secret secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        EnvironmentStoreRules.Secret(next, secret, expectedRevision, audit);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var old = await db.Set<Secret>().SingleOrDefaultAsync(s => s.TenantId == secret.TenantId && s.SecretId == secret.SecretId, ct).ConfigureAwait(false);
            if ((old?.Revision ?? 0) != expectedRevision || old?.Status == SecretStatus.Deleted || old is not null && old.CreatedAt != secret.CreatedAt) return false;
            if (old is null) db.Add(secret); else db.Entry(old).CurrentValues.SetValues(secret); return true;
        }, ct, initiator);
    }
    public ValueTask<bool> TryDeleteSecretAsync(TenantSecurityState next, long expectedSecurityRevision, Guid secret, long expectedRevision,
        AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var tenant = next.Tenant.TenantId;
            var old = await db.Set<Secret>().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SecretId == secret && s.Status != SecretStatus.Deleted, ct).ConfigureAwait(false);
            if (old?.Revision != expectedRevision) return false;
            if (await db.Set<EnvironmentVariableRecord>().AnyAsync(v => v.TenantId == tenant && v.SecretId == secret, ct).ConfigureAwait(false)
                || await db.Set<SpaceAddedBindingRecord>().AnyAsync(b => b.TenantId == tenant && b.Kind == SpaceBindingKind.Secret && b.ResourceKey == secret.ToString("D"), ct).ConfigureAwait(false)) throw new SecurityConflictException("secret_has_references");
            if (next.Assignments.Any(a => a.Scope == AccessScope.ForResource(tenant, "secret", secret.ToString("D")))) throw new ArgumentException("Retire Secret assignments on deletion.");
            db.Entry(old!).CurrentValues.SetValues(old! with { Status = SecretStatus.Deleted, ProtectedPayload = "", Revision = checked(old.Revision + 1), UpdatedAt = audit.At }); return true;
        }, ct, initiator);
    public async ValueTask<SpaceEnvironment?> FindEnvironmentAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<SpaceEnvironmentRecord>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SpaceId == space && e.EnvironmentId == environment && e.DeletedAt == null, ct).ConfigureAwait(false))?.Domain();
    }
    public async ValueTask<IReadOnlyList<SpaceEnvironment>> ListEnvironmentsAsync(Guid tenant, Guid space, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return (await db.Set<SpaceEnvironmentRecord>().AsNoTracking().Where(e => e.TenantId == tenant && e.SpaceId == space && e.DeletedAt == null).OrderBy(e => e.EnvironmentId).ToArrayAsync(ct).ConfigureAwait(false)).Select(e => e.Domain()).ToArray();
    }
    public async ValueTask<IReadOnlyList<EnvironmentVariable>> ReadVariablesAsync(Guid tenant, Guid space, Guid environment, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Set<EnvironmentVariableRecord>().AsNoTracking().Where(v => v.TenantId == tenant && v.SpaceId == space && v.EnvironmentId == environment
            && db.Set<SpaceEnvironmentRecord>().Any(e => e.TenantId == tenant && e.SpaceId == space && e.EnvironmentId == environment && e.DeletedAt == null)).OrderBy(v => v.Name).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(v => v.Domain()).ToArray();
    }
    public ValueTask<bool> TrySaveEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, SpaceEnvironment environment, long expectedSpaceRevision,
        long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        EnvironmentStoreRules.Environment(next, environment, expectedRevision, audit);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var e = environment;
            if (!await SpaceRevision(db, e.TenantId, e.SpaceId, expectedSpaceRevision, ct).ConfigureAwait(false)) return false;
            var old = await db.Set<SpaceEnvironmentRecord>().SingleOrDefaultAsync(x => x.TenantId == e.TenantId && x.SpaceId == e.SpaceId && x.EnvironmentId == e.EnvironmentId, ct).ConfigureAwait(false);
            if ((old?.Revision ?? 0) != expectedRevision || old?.DeletedAt is not null || old is not null && old.CreatedAt != e.CreatedAt) return false;
            if (await db.Set<SpaceEnvironmentRecord>().AnyAsync(x => x.TenantId == e.TenantId && x.SpaceId == e.SpaceId && x.EnvironmentId != e.EnvironmentId && x.DeletedAt == null && x.NormalizedName == e.NormalizedName, ct).ConfigureAwait(false)) throw new SecurityConflictException("environment_name_exists");
            if (old is null && await db.Set<SpaceEnvironmentRecord>().CountAsync(x => x.TenantId == e.TenantId && x.SpaceId == e.SpaceId && x.DeletedAt == null, ct).ConfigureAwait(false) >= EnvironmentStoreRules.MaxProfiles) throw new SecurityConflictException("environment_limit");
            if (old is null) db.Add(SpaceEnvironmentRecord.Row(e)); else db.Entry(old).CurrentValues.SetValues(SpaceEnvironmentRecord.Row(e)); return true;
        }, ct, initiator);
    }
    public ValueTask<bool> TryDeleteEnvironmentAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment, long expectedSpaceRevision,
        long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var tenant = next.Tenant.TenantId;
            if (!await SpaceRevision(db, tenant, space, expectedSpaceRevision, ct).ConfigureAwait(false)) return false;
            var old = await db.Set<SpaceEnvironmentRecord>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SpaceId == space && e.EnvironmentId == environment && e.DeletedAt == null, ct).ConfigureAwait(false);
            if (old?.Revision != expectedRevision) return false;
            await db.Set<EnvironmentVariableRecord>().Where(v => v.TenantId == tenant && v.SpaceId == space && v.EnvironmentId == environment).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.Entry(old!).CurrentValues.SetValues(old! with { DeletedAt = audit.At, UpdatedAt = audit.At, Revision = checked(old.Revision + 1) }); return true;
        }, ct, initiator);
    public ValueTask<bool> TrySaveVariableAsync(TenantSecurityState next, long expectedSecurityRevision, EnvironmentVariable variable, long expectedSpaceRevision,
        long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default)
    {
        EnvironmentStoreRules.Variable(next, variable, expectedRevision, audit);
        return WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var v = variable;
            var env = await VariableParent(db, v.TenantId, v.SpaceId, v.EnvironmentId, expectedSpaceRevision, expectedEnvironmentRevision, ct).ConfigureAwait(false);
            if (env is null) return false;
            var old = await db.Set<EnvironmentVariableRecord>().SingleOrDefaultAsync(x => x.TenantId == v.TenantId && x.EnvironmentId == v.EnvironmentId && x.Name == v.Name, ct).ConfigureAwait(false);
            if ((old?.Revision ?? 0) != expectedRevision || old is not null && (old.CreatedAt != v.CreatedAt || old.SpaceId != v.SpaceId)) return false;
            if (v.SecretId is { } secret && !await db.Set<Secret>().AnyAsync(s => s.TenantId == v.TenantId && s.SecretId == secret && s.Status == SecretStatus.Active, ct).ConfigureAwait(false)
                || v.CredentialId is { } credential && !await OwnedCredentials(db, v.TenantId).AnyAsync(c => c.CredentialId == credential.ToString("D") && c.Status == "Active", ct).ConfigureAwait(false)) throw new AccessDeniedException("resource_unavailable");
            if (old is null && await db.Set<EnvironmentVariableRecord>().CountAsync(x => x.TenantId == v.TenantId && x.EnvironmentId == v.EnvironmentId, ct).ConfigureAwait(false) >= EnvironmentStoreRules.MaxVariables) throw new SecurityConflictException("variable_limit");
            if (old is null) db.Add(EnvironmentVariableRecord.Row(v)); else db.Entry(old).CurrentValues.SetValues(EnvironmentVariableRecord.Row(v));
            db.Entry(env).CurrentValues.SetValues(env with { Revision = checked(env.Revision + 1), UpdatedAt = audit.At }); return true;
        }, ct, initiator);
    }
    public ValueTask<bool> TryDeleteVariableAsync(TenantSecurityState next, long expectedSecurityRevision, Guid space, Guid environment, string name,
        long expectedSpaceRevision, long expectedEnvironmentRevision, long expectedRevision, AuthorizationAudit audit, HumanSessionReference? initiator = null, CancellationToken ct = default) =>
        WriteTenantResourceAsync(next, expectedSecurityRevision, audit, async db =>
        {
            var tenant = next.Tenant.TenantId;
            var env = await VariableParent(db, tenant, space, environment, expectedSpaceRevision, expectedEnvironmentRevision, ct).ConfigureAwait(false);
            if (env is null) return false;
            var old = await db.Set<EnvironmentVariableRecord>().SingleOrDefaultAsync(v => v.TenantId == tenant && v.SpaceId == space && v.EnvironmentId == environment && v.Name == name, ct).ConfigureAwait(false);
            if (old?.Revision != expectedRevision) return false;
            db.Remove(old!); db.Entry(env).CurrentValues.SetValues(env with { Revision = checked(env.Revision + 1), UpdatedAt = audit.At }); return true;
        }, ct, initiator);
    private static Task<bool> SpaceRevision(AgentCoreDbContext db, Guid tenant, Guid space, long revision, CancellationToken ct) =>
        db.Set<SpaceRecord>().AnyAsync(s => s.TenantId == tenant && s.SpaceId == space && s.Revision == revision && s.DeletedAt == null, ct);
    private static async Task<SpaceEnvironmentRecord?> VariableParent(AgentCoreDbContext db, Guid tenant, Guid space, Guid environment, long spaceRevision, long environmentRevision, CancellationToken ct)
    {
        if (!await SpaceRevision(db, tenant, space, spaceRevision, ct).ConfigureAwait(false)) return null;
        return await db.Set<SpaceEnvironmentRecord>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SpaceId == space && e.EnvironmentId == environment && e.Revision == environmentRevision && e.DeletedAt == null, ct).ConfigureAwait(false);
    }
}
