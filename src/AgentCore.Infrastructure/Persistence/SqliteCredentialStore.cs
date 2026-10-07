using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteCredentialStore(IDbContextFactory<AgentCoreDbContext> contexts) : ICredentialStore, IAgentCredentialBindingStore
{
    public async ValueTask<IReadOnlyList<Credential>> ListAsync(CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.Credentials.AsNoTracking().OrderBy(c => c.DisplayName).ToArrayAsync(ct)).Select(Map).ToArray(); }
    public async ValueTask<Credential?> GetAsync(Guid id, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); var row = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(c => c.CredentialId == id.ToString("D"), ct); return row is null ? null : Map(row); }
    public async ValueTask SaveAsync(Credential c, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Credentials.SingleOrDefaultAsync(x => x.CredentialId == c.CredentialId.ToString("D"), ct);
        if ((row?.Revision ?? 0) != expectedRevision || c.Revision != expectedRevision + 1 || row is not null && row.Kind != c.Kind.ToString()) throw AgentCoreErrors.Conflict("Credential revision is stale.");
        if (row is null) { row = new(); db.Credentials.Add(row); }
        row.CredentialId = c.CredentialId.ToString("D"); row.DisplayName = c.DisplayName; row.Kind = c.Kind.ToString(); row.Status = c.Status.ToString();
        row.MetadataJson = JsonSerializer.Serialize(c.Metadata); row.AllowedOriginsJson = JsonSerializer.Serialize(c.AllowedOrigins);
        row.ProtectedPayload = c.ProtectedPayload; row.ProtectionVersion = c.ProtectionVersion; row.Revision = c.Revision;
        row.CreatedAtUtc = c.CreatedAtUtc.ToUnixTimeMilliseconds(); row.UpdatedAtUtc = c.UpdatedAtUtc.ToUnixTimeMilliseconds();
        await Save(db, ct);
    }
    public async ValueTask DeleteAsync(Guid id, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Credentials.SingleOrDefaultAsync(x => x.CredentialId == id.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Credential was not found.");
        if (row.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Credential revision is stale.");
        var count = await db.AgentCredentialBindings.CountAsync(b => b.CredentialId == row.CredentialId, ct);
        if (count != 0) throw AgentCoreErrors.Conflict($"Unbind {count} agent bindings before deleting this credential.");
        db.Credentials.Remove(row); await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<AgentCredentialBinding>> ListBindingsAsync(Guid? instanceId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var query = db.AgentCredentialBindings.AsNoTracking();
        if (instanceId is Guid id) query = query.Where(b => b.AgentInstanceId == id.ToString("D"));
        return (await query.OrderBy(b => b.Reference).ToArrayAsync(ct)).Select(b => new AgentCredentialBinding(Guid.Parse(b.BindingId), Guid.Parse(b.AgentInstanceId), Guid.Parse(b.CredentialId), b.Reference, b.Revision, DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(b.UpdatedAtUtc))).ToArray();
    }
    public async ValueTask BindAsync(AgentCredentialBinding b, long expectedInstanceRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireActive(db, b.AgentInstanceId, expectedInstanceRevision, ct);
        if (!await db.Credentials.AnyAsync(c => c.CredentialId == b.CredentialId.ToString("D"), ct)) throw AgentCoreErrors.NotFound("Credential was not found.");
        db.AgentCredentialBindings.Add(new() { BindingId = b.BindingId.ToString("D"), AgentInstanceId = b.AgentInstanceId.ToString("D"), CredentialId = b.CredentialId.ToString("D"), Reference = b.Reference, Revision = b.Revision, CreatedAtUtc = b.CreatedAtUtc.ToUnixTimeMilliseconds(), UpdatedAtUtc = b.UpdatedAtUtc.ToUnixTimeMilliseconds() });
        await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask UnbindAsync(Guid instanceId, Guid bindingId, long expectedRevision, long instanceRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await RequireActive(db, instanceId, instanceRevision, ct);
        var row = await db.AgentCredentialBindings.SingleOrDefaultAsync(b => b.BindingId == bindingId.ToString("D") && b.AgentInstanceId == instanceId.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Binding was not found.");
        if (row.Revision != expectedRevision) throw AgentCoreErrors.Conflict("Binding revision is stale.");
        db.AgentCredentialBindings.Remove(row); await Save(db, ct); await transaction.CommitAsync(ct);
    }
    public async ValueTask DeleteBindingsAsync(Guid instanceId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); await db.AgentCredentialBindings.Where(b => b.AgentInstanceId == instanceId.ToString("D")).ExecuteDeleteAsync(ct); }
    private static async Task RequireActive(AgentCoreDbContext db, Guid id, long revision, CancellationToken ct)
    {
        var row = await db.AgentInstances.AsNoTracking().SingleOrDefaultAsync(a => a.InstanceId == id.ToString("D"), ct) ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
        if (row.Lifecycle != "Active") throw AgentCoreErrors.Forbidden("Archived agent credentials are read-only.");
        if (row.Revision != revision) throw AgentCoreErrors.Conflict("Agent instance revision is stale.");
    }
    private static async Task Save(AgentCoreDbContext db, CancellationToken ct)
    { try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Credential or binding changed concurrently or is already bound."); } }
    private static Credential Map(CredentialRecord c) => new(Guid.Parse(c.CredentialId), c.DisplayName, Enum.Parse<CredentialKind>(c.Kind), Enum.Parse<CredentialStatus>(c.Status), CredentialRules.Metadata(JsonSerializer.Deserialize<Dictionary<string,string>>(c.MetadataJson)), CredentialRules.Origins(JsonSerializer.Deserialize<string[]>(c.AllowedOriginsJson)), c.ProtectedPayload, c.ProtectionVersion, c.Revision, DateTimeOffset.FromUnixTimeMilliseconds(c.CreatedAtUtc), DateTimeOffset.FromUnixTimeMilliseconds(c.UpdatedAtUtc));
}
