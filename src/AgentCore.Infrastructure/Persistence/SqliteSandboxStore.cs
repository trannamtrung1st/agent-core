using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Admin;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SandboxProfileRow { public string Id { get; set; } = ""; public long Revision { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxLeaseRow { public string SessionId { get; set; } = ""; public string AgentInstanceId { get; set; } = ""; public long Revision { get; set; } public long ExpiresAt { get; set; } public int State { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxExecutionRow { public string Id { get; set; } = ""; public string SessionId { get; set; } = ""; public string AgentRunId { get; set; } = ""; public string ToolCallId { get; set; } = ""; public string Json { get; set; } = ""; }

public sealed class SqliteSandboxStore(IDbContextFactory<AgentCoreDbContext> factory) : ISandboxStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw AgentCoreErrors.Persistence("Sandbox record is invalid.");
    public async ValueTask<IReadOnlyList<SandboxProfile>> ListProfilesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Set<SandboxProfileRow>().AsNoTracking().ToListAsync(ct);
        return rows.GroupBy(r => r.Id).Select(g => Read<SandboxProfile>(g.MaxBy(r => r.Revision)!.Json)).OrderBy(p => p.Name).ToArray();
    }
    public async ValueTask<SandboxProfile?> GetProfileAsync(string id, long? revision = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<SandboxProfileRow>().AsNoTracking().Where(r => r.Id == id && (revision == null || r.Revision == revision)).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
        return row is null ? null : Read<SandboxProfile>(row.Json);
    }
    public async ValueTask SaveProfileAsync(SandboxProfile profile, long expectedRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        profile.Validate(); await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var latest = await db.Set<SandboxProfileRow>().Where(r => r.Id == profile.Id).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
            if ((latest?.Revision ?? 0) != expectedRevision || profile.Revision != expectedRevision + 1) throw AgentCoreErrors.Conflict("Sandbox profile changed. Reload before saving.");
            db.Add(new SandboxProfileRow { Id = profile.Id, Revision = profile.Revision, Json = JsonSerializer.Serialize(profile, Json) });
            if (history is not null) AdminEventPersistence.StageAppend(db,history,Guid.CreateVersion7());
            await db.SaveChangesAsync(ct);
        } finally { _gate.Release(); }
    }
    public async ValueTask<SandboxLease?> GetLeaseAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(r => r.SessionId == sessionId.ToString("D"), ct);
        return row is null ? null : Read<SandboxLease>(row.Json);
    }
    public async ValueTask<IReadOnlyList<SandboxLease>> ListLeasesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Set<SandboxLeaseRow>().AsNoTracking().Where(r => r.State != (int)SandboxLeaseState.Terminated).ToListAsync(ct)).Select(r => Read<SandboxLease>(r.Json)).ToArray();
    }
    public async ValueTask SaveLeaseAsync(SandboxLease lease, long expectedRevision, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(r => r.SessionId == lease.SessionId.ToString("D"), ct);
            if ((row?.Revision ?? 0) != expectedRevision || lease.Revision != expectedRevision + 1 || row is not null && row.AgentInstanceId != lease.AgentInstanceId.ToString("D"))
                throw AgentCoreErrors.Conflict("Sandbox generation changed.");
            if (row is null) { row = new() { SessionId = lease.SessionId.ToString("D"), AgentInstanceId = lease.AgentInstanceId.ToString("D") }; db.Add(row); }
            row.Revision = lease.Revision; row.State = (int)lease.State; row.ExpiresAt = lease.ExpiresAt.ToUnixTimeMilliseconds(); row.Json = JsonSerializer.Serialize(lease, Json);
            await db.SaveChangesAsync(ct);
        } finally { _gate.Release(); }
    }
    public async ValueTask<SandboxExecution?> GetExecutionAsync(Guid sessionId, Guid executionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == executionId.ToString("D") && r.SessionId == sessionId.ToString("D"), ct);
        return row is null ? null : Read<SandboxExecution>(row.Json);
    }
    public async ValueTask<SandboxExecution?> GetOperationAsync(Guid sessionId, Guid agentRunId, string toolCallId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.SessionId == sessionId.ToString("D") && r.AgentRunId == agentRunId.ToString("D") && r.ToolCallId == toolCallId, ct);
        return row is null ? null : Read<SandboxExecution>(row.Json);
    }
    public async ValueTask SaveExecutionAsync(SandboxExecution execution, bool create, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(r => r.Id == execution.Id.ToString("D"), ct);
            var old = row is null ? null : Read<SandboxExecution>(row.Json);
            if (create ? old is not null || await db.Set<SandboxExecutionRow>().AnyAsync(r => r.SessionId == execution.SessionId.ToString("D") && r.AgentRunId == execution.AgentRunId.ToString("D") && r.ToolCallId == execution.ToolCallId, ct)
                : old is null || old.SessionId != execution.SessionId || old.Generation != execution.Generation || old.ActionHash != execution.ActionHash)
                throw AgentCoreErrors.Conflict("Sandbox operation receipt changed.");
            if (row is null) { row = new() { Id = execution.Id.ToString("D"), SessionId = execution.SessionId.ToString("D"), AgentRunId = execution.AgentRunId.ToString("D"), ToolCallId = execution.ToolCallId }; db.Add(row); }
            row.Json = JsonSerializer.Serialize(execution, Json); await db.SaveChangesAsync(ct);
        } finally { _gate.Release(); }
    }
}
