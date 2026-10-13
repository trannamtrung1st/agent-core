using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteEmailWatchStore(IDbContextFactory<AgentCoreDbContext> contexts) : IEmailWatchStore
{
    private static EmailWatchState Read(EmailWatchRecord r) => JsonSerializer.Deserialize<EmailWatchState>(r.StateJson)!;
    private static void Write(EmailWatchRecord r, EmailWatchState s)
    { r.Enabled = s.Enabled; r.RuntimeRevision = s.RuntimeRevision; r.NextCheckAtUtc = s.NextCheckAtUtc?.ToUnixTimeMilliseconds() ?? long.MaxValue; r.StateJson = JsonSerializer.Serialize(s); }
    private static async Task<Credential> Account(AgentCoreDbContext db, Guid id, CancellationToken ct) => EmailWatchTransitions.Require(
        (await db.Credentials.Include(c => c.EmailAccount).SingleOrDefaultAsync(c => c.CredentialId == id.ToString("D"), ct)) is { } r ? SqliteCredentialStore.Map(r) : null);
    public async ValueTask<EmailWatchState> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await Account(db, id, ct);
        var r = await db.EmailWatches.AsNoTracking().SingleOrDefaultAsync(r => r.AccountId == id.ToString("D"), ct); return r is null ? new(id) : Read(r);
    }
    public async ValueTask<EmailWatchState> ConfigureAsync(Guid id, long expected, bool enabled, int interval, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Account(db, id, ct);
        var r = await db.EmailWatches.SingleOrDefaultAsync(r => r.AccountId == id.ToString("D"), ct);
        var s = EmailWatchTransitions.Configure(r is null ? new(id) : Read(r), expected, enabled, interval, now);
        if (r is null) { r = new() { AccountId = id.ToString("D") }; db.EmailWatches.Add(r); }
        Write(r, s); await Save(db, ct); await tx.CommitAsync(ct); return s;
    }
    public async ValueTask CheckNowAsync(Guid id, long expected, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var account = await Account(db, id, ct);
        var r = await db.EmailWatches.SingleOrDefaultAsync(r => r.AccountId == id.ToString("D"), ct);
        var s = r is null ? new(id) : Read(r); EmailWatchTransitions.Revision(s, expected);
        if (!s.Enabled || !EmailWatchTransitions.Usable(account)) throw AgentCoreErrors.Conflict("Enable monitoring on an available account before checking.");
        if (s.LeaseExpiresAtUtc > now) return;
        Write(r!, s with { NextCheckAtUtc = now, Paused = false, RuntimeRevision = s.RuntimeRevision + 1 });
        await Save(db, ct); await tx.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<EmailWatchClaim>> ClaimDueAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.EmailWatches.Where(r => r.Enabled && r.NextCheckAtUtc <= now.ToUnixTimeMilliseconds())
            .OrderBy(r => r.NextCheckAtUtc).ThenBy(r => r.AccountId).Take(Math.Clamp(limit, 1, 8)).ToArrayAsync(ct);
        var result = new List<EmailWatchClaim>();
        foreach (var r in rows)
        {
            var c = await Account(db, Guid.Parse(r.AccountId), ct); var s = Read(r);
            if (s.LeaseExpiresAtUtc > now) continue;
            if (!EmailWatchTransitions.Usable(c) || s.Paused && s.AuthorizationFingerprint == EmailWatchTransitions.Fingerprint(c))
            { Write(r, s with { NextCheckAtUtc = now.AddMinutes(15), RuntimeRevision = s.RuntimeRevision + 1 }); continue; }
            s = EmailWatchTransitions.Claim(s, c, now); Write(r, s); result.Add(new(s, c));
        }
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateConcurrencyException) { return []; }
        return result;
    }
    public async ValueTask<bool> CommitAsync(EmailWatchClaim claim, EmailWatchBatch batch, IReadOnlyList<EmailWatchNotification> notifications, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var r = await db.EmailWatches.SingleOrDefaultAsync(r => r.AccountId == claim.State.AccountId.ToString("D"), ct);
        if (r is null) return false;
        Credential? c;
        try { c = await Account(db, claim.State.AccountId, ct); } catch (AgentCoreException) { return false; }
        var s = Read(r); if (!EmailWatchTransitions.Valid(s, claim, c, now)) return false;
        var emitted = false;
        foreach (var n in notifications)
        {
            if (batch.CursorGap || n.AccountId != s.AccountId || n.Generation != s.Generation || n.AuthorizationFingerprint != s.AuthorizationFingerprint
                || !await BindingValid(db, n, ct) || await db.CoreEvents.AnyAsync(e => e.DedupeKey == n.Event.DedupeKey, ct)) continue;
            CoreEventPersistence.Stage(db, n.Event);
            db.EmailWatchEvents.Add(new() { EventId = n.Event.EventId.ToString("D"), AuthorityJson = JsonSerializer.Serialize(n) }); emitted = true;
        }
        Write(r, EmailWatchTransitions.Success(s, batch, emitted, now));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; } catch (DbUpdateConcurrencyException) { return false; }
    }
    private static async Task<bool> BindingValid(AgentCoreDbContext db, EmailWatchNotification n, CancellationToken ct)
    {
        var reference = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(n.Event.DataJson)!["accountRef"].GetString();
        if (!await db.AgentCredentialBindings.AnyAsync(b => b.BindingId == n.BindingId.ToString("D") && b.Revision == n.BindingRevision
            && b.CredentialId == n.AccountId.ToString("D") && b.Reference == reference && b.AgentInstanceId == n.Event.Owner.AgentInstanceId.ToString("D")
            && db.AgentInstances.Any(i => i.InstanceId == b.AgentInstanceId && i.Lifecycle == "Active"), ct)) return false;
        var rows = await db.Automations.Include(a => a.Triggers).Where(a => a.AgentInstanceId == n.Event.Owner.AgentInstanceId.ToString("D")
            && a.ProfileId == n.Event.Owner.ProfileId.ToString("D") && a.Status == (int)AgentCore.Domain.Triggers.AutomationStatus.Active).ToArrayAsync(ct);
        return rows.Select(TriggerStoreMapping.ToRegistration).Any(a => a.Triggers.Any(t => t.Enabled && t.Configuration is AgentCore.Domain.Triggers.CoreEventTrigger { CoreEventKey: "email.message.received" }));
    }
    public async ValueTask RecordFailureAsync(EmailWatchClaim claim, SafeEmailWatchFailure failure, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var r = await db.EmailWatches.SingleOrDefaultAsync(r => r.AccountId == claim.State.AccountId.ToString("D"), ct);
        if (r is null) return;
        var s = Read(r); if (s.LeaseId != claim.State.LeaseId || s.RuntimeRevision != claim.State.RuntimeRevision) return;
        Write(r, EmailWatchTransitions.Failure(s, failure, now)); try { await db.SaveChangesAsync(ct); } catch (DbUpdateConcurrencyException) { }
    }
    public async ValueTask<bool> CanDeliverAsync(Guid eventId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var receipt = await db.EmailWatchEvents.AsNoTracking().SingleOrDefaultAsync(r => r.EventId == eventId.ToString("D"), ct); if (receipt is null) return false;
        var n = JsonSerializer.Deserialize<EmailWatchNotification>(receipt.AuthorityJson)!;
        var r = await db.EmailWatches.AsNoTracking().SingleOrDefaultAsync(r => r.AccountId == n.AccountId.ToString("D"), ct); if (r is null) return false;
        var s = Read(r); Credential c; try { c = await Account(db, n.AccountId, ct); } catch (AgentCoreException) { return false; }
        return s.Enabled && s.Generation == n.Generation && EmailWatchTransitions.Usable(c) && EmailWatchTransitions.Fingerprint(c) == n.AuthorizationFingerprint && await BindingValid(db, n, ct);
    }
    private static async Task Save(AgentCoreDbContext db, CancellationToken ct)
    { try { await db.SaveChangesAsync(ct); } catch (DbUpdateException) { throw AgentCoreErrors.Conflict("Monitoring changed concurrently. Reload and review your changes."); } }
}
