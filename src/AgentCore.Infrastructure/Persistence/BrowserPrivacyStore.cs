using System.Text.Json;
using AgentCore.Application.Ports;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class BrowserPrivacyRecord
{
    public int Id { get; set; }
    public long Revision { get; set; }
    public string PolicyJson { get; set; } = "";
}

public sealed class SqliteBrowserPrivacyStore(IDbContextFactory<AgentCoreDbContext> contexts) : IBrowserPrivacyStore
{
    public bool IsDurable => true;
    public async ValueTask<BrowserScreenshotPolicy?> ReadAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.BrowserPrivacy.AsNoTracking().SingleOrDefaultAsync(ct);
        return row is null ? null : JsonSerializer.Deserialize<BrowserScreenshotPolicy>(row.PolicyJson);
    }

 }

public sealed class InMemoryBrowserPrivacyStore : IBrowserPrivacyStore
{
    public bool IsDurable => false;
    internal object Gate { get; } = new();
    private BrowserScreenshotPolicy? _policy;
    public ValueTask<BrowserScreenshotPolicy?> ReadAsync(CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (Gate) return ValueTask.FromResult(_policy); }
    internal bool TrySave(BrowserScreenshotPolicy policy, long expectedRevision)
    {
        lock (Gate) { if ((_policy?.Revision ?? 0) != expectedRevision) return false; _policy = policy; return true; }
    }
}
