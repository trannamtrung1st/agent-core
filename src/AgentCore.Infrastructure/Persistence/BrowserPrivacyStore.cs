using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Admin;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Persistence;

public sealed class BrowserPrivacyRecord
{
    public int Id { get; set; }
    public long Revision { get; set; }
    public string PolicyJson { get; set; } = "";
}

public sealed class SqliteBrowserPrivacyStore(IDbContextFactory<AgentCoreDbContext> contexts, IIdGenerator ids) : IBrowserPrivacyStore
{
    public bool IsDurable => true;
    public async ValueTask<BrowserScreenshotPolicy?> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.BrowserPrivacy.AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? null : JsonSerializer.Deserialize<BrowserScreenshotPolicy>(row.PolicyJson);
    }

    public async ValueTask SaveAsync(BrowserScreenshotPolicy policy, long expectedRevision, AdminEventAppend audit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.BrowserPrivacy.SingleOrDefaultAsync(ct);
        if ((row?.Revision ?? 0) != expectedRevision) throw AgentCoreErrors.Conflict("Browser privacy changed. Reload the saved policy before retrying.");
        if (row is null) { row = new() { Id = 1 }; db.BrowserPrivacy.Add(row); }
        row.Revision = policy.Revision;
        row.PolicyJson = JsonSerializer.Serialize(policy);
        AdminEventPersistence.StageAppend(db, audit, ids.NewId());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Browser privacy changed. Reload the saved policy before retrying."); }
        catch (DbUpdateException failure) when (expectedRevision == 0 && failure.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 })
        { throw AgentCoreErrors.Conflict("Browser privacy changed. Reload the saved policy before retrying."); }
    }
}

public sealed class InMemoryBrowserPrivacyStore(InMemoryAdminEventStore events) : IBrowserPrivacyStore
{
    public bool IsDurable => false;
    private readonly object _gate = new();
    private BrowserScreenshotPolicy? _policy;
    public ValueTask<BrowserScreenshotPolicy?> ReadAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_policy); }
    public ValueTask SaveAsync(BrowserScreenshotPolicy policy, long expectedRevision, AdminEventAppend audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if ((_policy?.Revision ?? 0) != expectedRevision) throw AgentCoreErrors.Conflict("Browser privacy changed. Reload the saved policy before retrying.");
            events.AppendWithinLock(audit);
            _policy = policy;
        }
        return ValueTask.CompletedTask;
    }
}
