using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Admin;
using AgentCore.Application.Sessions;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteCredentialStore(IDbContextFactory<AgentCoreDbContext> contexts) : ICredentialStore, IAgentCredentialBindingStore
{
    public async ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return (await Unowned(db).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking().OrderBy(c => c.DisplayName).ToArrayAsync(ct)).Select(Map).ToArray(); }
    public async ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await Unowned(db).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == id.ToString("D"), ct); return row is null ? null : Map(row); }
    public async ValueTask SaveAsync(Credential c, long expectedRevision, CancellationToken ct = default, long? expectedSecretRevision = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireLegacyCredential(db, c.CredentialId, ct);
        var row = await db.Credentials.Include(c => c.EmailAccount).Include(c => c.DriveAccount).SingleOrDefaultAsync(x => x.CredentialId == c.CredentialId.ToString("D"), ct);
        if (expectedSecretRevision is not null && row?.SecretRevision != expectedSecretRevision || (row?.Revision ?? 0) != expectedRevision || c.Revision != expectedRevision + 1 || row is not null && (row.Kind != c.Kind.ToString() || row.Purpose != c.Purpose.ToString())) throw AgentCoreErrors.Conflict("Credential revision is stale.");
        if ((c.Purpose == CredentialPurpose.EmailAccount) != (c.EmailAccount is not null) || c.Purpose == CredentialPurpose.EmailAccount && (c.Kind != (c.EmailAccount?.AuthMethod == EmailAuthMethod.GoogleOAuth2 ? CredentialKind.Token : CredentialKind.Password) || c.AllowedOrigins.Count != 0)) throw AgentCoreErrors.Validation("Invalid credential purpose/profile.");
        c.EmailAccount?.Validate();
        if (!Enum.IsDefined(c.Purpose) || (c.Purpose == CredentialPurpose.GoogleDriveAccount) != (c.DriveAccount is not null)
            || c.Purpose == CredentialPurpose.GoogleDriveAccount && (c.Kind != CredentialKind.Token || c.AllowedOrigins.Count != 0)) throw AgentCoreErrors.Validation("Invalid Drive credential purpose/profile.");
        c.DriveAccount?.Validate();
        if ((c.Purpose == CredentialPurpose.ContainerRegistry) != (c.ContainerRegistry is not null)
            || c.Purpose == CredentialPurpose.ContainerRegistry && (c.Kind is not (CredentialKind.Password or CredentialKind.Token) || c.AllowedOrigins.Count != 0))
            throw AgentCoreErrors.Validation("Invalid Container Registry purpose/profile.");
        if (c.ContainerRegistry is not null) c = c with { ContainerRegistry=c.ContainerRegistry.Validate() };
        StageValue(db, row, c);
        await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask<bool> TryRotateOAuthSecretAsync(Guid id, long expectedRevision, long expectedSecretRevision, string payload, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        return await Unowned(db).Where(c => c.CredentialId == id.ToString("D") && c.Revision == expectedRevision
            && c.SecretRevision == expectedSecretRevision && c.Status == "Active" && c.ProtectedPayload != "" && c.Kind == "Token" && (c.Purpose == "EmailAccount" || c.Purpose == "GoogleDriveAccount"))
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ProtectedPayload, payload).SetProperty(c => c.SecretRevision, expectedSecretRevision + 1), ct) == 1;
    }
    public async ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireLegacyCredential(db, id, ct);
        var row = await db.Credentials.Include(c => c.EmailAccount).Include(c => c.DriveAccount).SingleOrDefaultAsync(x => x.CredentialId == id.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
        if (row.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Credential revision is stale.");
        var count = await db.AgentCredentialBindings.CountAsync(b => b.CredentialId == row.CredentialId, ct);
        if (count != 0) throw AgentCoreErrors.Conflict($"Unbind {count} agent bindings before deleting this credential.");
        db.Credentials.Remove(row); await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var query = db.AgentCredentialBindings.AsNoTracking()
            .Where(b => !db.Set<IamSystemIdentityRow>().Any(i => i.AgentInstanceId == b.AgentInstanceId));
        if (instanceId is Guid id) query = query.Where(b => b.AgentInstanceId == id.ToString("D"));
        return (await query.OrderBy(b => b.Reference).ToArrayAsync(ct)).Select(b => new AgentCredentialBinding(Guid.Parse(b.BindingId), Guid.Parse(b.AgentInstanceId), Guid.Parse(b.CredentialId), b.Reference, b.Revision, DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(b.UpdatedAtUtc))).ToArray();
    }
    public async ValueTask BindAsync(AgentCredentialBinding b, long expectedInstanceRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireActive(db, b.AgentInstanceId, expectedInstanceRevision, ct);
        await RequireLegacyCredential(db, b.CredentialId, ct);
        var credential = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == b.CredentialId.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
        if (credential.Purpose == "ContainerRegistry") throw AgentCoreErrors.Forbidden("Registry credentials cannot be bound to agents.");
        db.AgentCredentialBindings.Add(new() { BindingId = b.BindingId.ToString("D"), AgentInstanceId = b.AgentInstanceId.ToString("D"), CredentialId = b.CredentialId.ToString("D"), Reference = b.Reference, Revision = b.Revision, CreatedAtUtc = b.CreatedAtUtc.ToUnixTimeMilliseconds(), UpdatedAtUtc = b.UpdatedAtUtc.ToUnixTimeMilliseconds() });
        if (history is not null) AdminEventPersistence.StageAppend(db, history, Guid.NewGuid());
        await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long instanceRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireActive(db, instanceId, instanceRevision, ct);
        var row = await db.AgentCredentialBindings.SingleOrDefaultAsync(b => b.BindingId == bindingId.ToString("D") && b.AgentInstanceId == instanceId.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Binding was not found.");
        if (row.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Binding revision is stale.");
        db.AgentCredentialBindings.Remove(row);
        if (history is not null) AdminEventPersistence.StageAppend(db, history, Guid.NewGuid());
        await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireLegacyContext(db, instanceId, ct);
        await db.AgentCredentialBindings.Where(b => b.AgentInstanceId == instanceId.ToString("D")).ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }
    private static async Task RequireActive(AgentCoreDbContext db, Guid id, long revision, CancellationToken ct)
    {
        await RequireLegacyContext(db, id, ct);
        var row = await db.AgentInstances.SingleOrDefaultAsync(a => a.InstanceId == id.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Lifecycle != "Active") throw AgentCoreErrors.Forbidden("Archived agent credentials are read-only.");
        if (row.Revision != revision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
        row.Revision++;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    private static async Task RequireLegacyContext(AgentCoreDbContext db, Guid id, CancellationToken ct)
    {
        if (await db.Set<IamSystemIdentityRow>().AnyAsync(i => i.AgentInstanceId == id.ToString("D"), ct))
            throw new AccessDeniedException("tenant_context_required");
    }
    private static IQueryable<CredentialRecord> Unowned(AgentCoreDbContext db) => db.Credentials
        .Where(c => !db.Set<TenantCredentialRecord>().Any(owner => owner.CredentialId == c.CredentialId));
    private static async Task RequireLegacyCredential(AgentCoreDbContext db, Guid id, CancellationToken ct)
    {
        if (await db.Set<TenantCredentialRecord>().AnyAsync(owner => owner.CredentialId == id.ToString("D"), ct))
            throw new AccessDeniedException("tenant_context_required");
    }
    private static async Task Save(AgentCoreDbContext db, CancellationToken ct)
    { try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Credential or binding changed concurrently or is already bound."); } }
    internal static void StageValue(AgentCoreDbContext db, CredentialRecord? row, Credential c)
    {
        if (row is null) { row = new(); db.Credentials.Add(row); }
        row.CredentialId = c.CredentialId.ToString("D"); row.DisplayName = c.DisplayName; row.Kind = c.Kind.ToString(); row.Status = c.Status.ToString();
        row.Purpose = c.Purpose.ToString();
        row.ContainerRegistryJson = c.ContainerRegistry is null ? null : JsonSerializer.Serialize(c.ContainerRegistry);
        if (c.EmailAccount is not null) row.EmailAccount = new() { CredentialId = row.CredentialId, ProfileJson = JsonSerializer.Serialize(c.EmailAccount), GoogleSubject = c.EmailAccount.GoogleSubject };
        if (c.DriveAccount is not null) row.DriveAccount = new() { CredentialId = row.CredentialId, ProfileJson = JsonSerializer.Serialize(c.DriveAccount), GoogleSubject = c.DriveAccount.GoogleSubject };
        row.MetadataJson = JsonSerializer.Serialize(c.Metadata); row.AllowedOriginsJson = JsonSerializer.Serialize(c.AllowedOrigins);
        row.SecretRevision = c.SecretRevision; row.ProtectedPayload = c.ProtectedPayload; row.ProtectionVersion = c.ProtectionVersion; row.Revision = c.Revision;
        row.CreatedAtUtc = c.CreatedAtUtc.ToUnixTimeMilliseconds(); row.UpdatedAtUtc = c.UpdatedAtUtc.ToUnixTimeMilliseconds();
    }
    internal static Credential Map(CredentialRecord c) => new(Guid.Parse(c.CredentialId), c.DisplayName, Enum.Parse<CredentialKind>(c.Kind), Enum.Parse<CredentialStatus>(c.Status), CredentialRules.Metadata(JsonSerializer.Deserialize<Dictionary<string,string>>(c.MetadataJson)), CredentialRules.Origins(JsonSerializer.Deserialize<string[]>(c.AllowedOriginsJson)), c.ProtectedPayload, c.ProtectionVersion, c.Revision, DateTimeOffset.FromUnixTimeMilliseconds(c.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAtUtc), Enum.Parse<CredentialPurpose>(c.Purpose), c.EmailAccount is null ? null : JsonSerializer.Deserialize<EmailAccountProfile>(c.EmailAccount.ProfileJson), c.SecretRevision, c.DriveAccount is null ? null : JsonSerializer.Deserialize<DriveAccountProfile>(c.DriveAccount.ProfileJson), c.ContainerRegistryJson is null ? null : JsonSerializer.Deserialize<ContainerRegistryProfile>(c.ContainerRegistryJson));
}
