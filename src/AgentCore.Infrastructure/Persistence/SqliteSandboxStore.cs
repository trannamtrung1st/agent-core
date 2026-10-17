using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Admin;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SandboxProfileRow { public string SearchText { get; set; } = ""; public Guid? TenantId { get; set; } public string Id { get; set; } = ""; public long Revision { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxVerificationRow { public Guid? TenantId { get; set; } public Guid? AttemptId { get; set; } public string ProfileId { get; set; } = ""; public long ProfileRevision { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxInstanceVerificationRow { public string InstanceId { get; set; } = ""; public long InstanceRevision { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxLeaseRow { public Guid? TenantId { get; set; } public Guid? ExecutorPrincipalId { get; set; } public string? ProfileId { get; set; } public long? ProfileRevision { get; set; } public string SessionId { get; set; } = ""; public string AgentInstanceId { get; set; } = ""; public long Revision { get; set; } public long ExpiresAt { get; set; } public int State { get; set; } public string Json { get; set; } = ""; }
public sealed class SandboxExecutionRow { public Guid? ApprovalRequestId { get; set; } public Guid? OperationId { get; set; } public string? ApprovedActionHash { get; set; } public Guid? TenantId { get; set; } public Guid? ExecutorPrincipalId { get; set; } public string? AgentInstanceId { get; set; } public string? OwnedRunId { get; set; } public string? LeaseSessionId { get; set; } public long Revision { get; set; } public string Id { get; set; } = ""; public string SessionId { get; set; } = ""; public string AgentRunId { get; set; } = ""; public string ToolCallId { get; set; } = ""; public string Json { get; set; } = ""; }

public sealed partial class SqliteSandboxStore(IDbContextFactory<AgentCoreDbContext> factory) : ISandboxStore
{
    public async ValueTask<ResourceStateObservation> ReadSessionStateAsync(AgentRunOwner owner, Guid sessionId, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        if (owner.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
        var lease = await GetLeaseAsync(sessionId, ct);
        return SandboxStateProjection.Observe(lease is not null && lease.AgentInstanceId == owner.AgentInstanceId
            && lease.State != SandboxLeaseState.Terminated ? [lease] : [], observedAt);
    }
    public async ValueTask<ResourceStateObservation> ReadStateAsync(AgentRunOwner owner, DateTimeOffset observedAt, CancellationToken ct = default)
    {
        if (owner.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Set<SandboxLeaseRow>().AsNoTracking().Where(r => r.AgentInstanceId == owner.AgentInstanceId.ToString("D")
            && r.TenantId == null && r.State != (int)SandboxLeaseState.Terminated
            && !db.Set<TenantSessionRecord>().Any(s => s.SessionId == r.SessionId)
            && !db.Set<IamSystemIdentityRow>().Any(i => i.TenantId != null && i.AgentInstanceId == r.AgentInstanceId)).OrderBy(r => r.SessionId).Take(101).Select(r => r.Json).ToArrayAsync(ct);
        return SandboxStateProjection.Observe(rows.Take(100).Select(Read<SandboxLease>).ToArray(), observedAt, rows.Length > 100);
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Json) ?? throw AgentCoreErrors.Persistence("Sandbox record is invalid.");
    public async ValueTask<IReadOnlyList<SandboxProfile>> ListProfilesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = await db.Set<SandboxProfileRow>().AsNoTracking().Where(r => r.TenantId == null).ToListAsync(ct);
        return rows.GroupBy(r => r.Id).Select(g => Read<SandboxProfile>(g.MaxBy(r => r.Revision)!.Json)).OrderBy(p => p.Name).ToArray();
    }
    public async ValueTask<SandboxProfile?> GetProfileAsync(string id, long? revision = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Set<SandboxProfileRow>().AsNoTracking().Where(r => r.TenantId == null && r.Id == id && (revision == null || r.Revision == revision)).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
        return row is null ? null : Read<SandboxProfile>(row.Json);
    }
    public async ValueTask SaveProfileAsync(SandboxProfile profile, long expectedRevision, CancellationToken ct = default, AdminEventAppend? history = null)
    {
        profile.Validate(); if (profile.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (await db.Set<AgentCore.Infrastructure.Security.TenantSandboxProfileRecord>().AnyAsync(r => r.ProfileId == profile.Id, ct))
                throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
            var latest = await db.Set<SandboxProfileRow>().Where(r => r.Id == profile.Id).OrderByDescending(r => r.Revision).FirstOrDefaultAsync(ct);
            if ((latest?.Revision ?? 0) != expectedRevision || profile.Revision != expectedRevision + 1) throw AgentCoreErrors.Conflict("Sandbox profile changed. Reload before saving.");
            db.Add(new SandboxProfileRow { Id = profile.Id, Revision = profile.Revision, Json = JsonSerializer.Serialize(profile, Json) });
            if (history is not null) AdminEventPersistence.StageAppend(db,history,Guid.CreateVersion7());
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
        } finally { _gate.Release(); }
    }
    public async ValueTask<SandboxProfileVerification?> GetVerificationAsync(string id, long revision, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await db.Set<SandboxProfileRow>().AnyAsync(r => r.Id == id && r.TenantId != null, ct)) return null;
        var row = await db.Set<SandboxVerificationRow>().AsNoTracking().SingleOrDefaultAsync(r => r.ProfileId == id && r.ProfileRevision == revision, ct);
        return row is null ? null : Read<SandboxProfileVerification>(row.Json);
    }
    public async ValueTask SaveVerificationAsync(SandboxProfileVerification verification, CancellationToken ct = default)
    {
        if (verification.TenantId is not null || verification.InstanceId is not null || verification.InstanceRevision is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_scope_required");
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            if (!await db.Set<SandboxProfileRow>().AnyAsync(r => r.TenantId == null && r.Id == verification.ProfileId && r.Revision == verification.ProfileRevision, ct))
                throw AgentCoreErrors.NotFound("Verified profile revision was not found.");
            var row = await db.Set<SandboxVerificationRow>().SingleOrDefaultAsync(r => r.ProfileId == verification.ProfileId && r.ProfileRevision == verification.ProfileRevision, ct);
            if (row is null) { row = new() { ProfileId = verification.ProfileId, ProfileRevision = verification.ProfileRevision }; db.Add(row); }
            row.Json = JsonSerializer.Serialize(verification, Json); await db.SaveChangesAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<SandboxInstanceVerification?> GetInstanceVerificationAsync(Guid id, long revision, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await TenantInstance(db, id, ct)) return null;
        var row = await db.Set<SandboxInstanceVerificationRow>().AsNoTracking().SingleOrDefaultAsync(r => r.InstanceId == id.ToString("D") && r.InstanceRevision == revision, ct);
        return row is null ? null : Read<SandboxInstanceVerification>(row.Json);
    }
    public async ValueTask SaveInstanceVerificationAsync(SandboxInstanceVerification verification, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await TenantInstance(db, verification.InstanceId, ct) || verification.Result.TenantId is not null || verification.Result.InstanceId is not null
                || verification.Observations?.Any(v => v.TenantId is not null || v.InstanceId is not null) == true) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
            var row = await db.Set<SandboxInstanceVerificationRow>().SingleOrDefaultAsync(r => r.InstanceId == verification.InstanceId.ToString("D") && r.InstanceRevision == verification.InstanceRevision, ct);
            if (row is null) { row = new() { InstanceId = verification.InstanceId.ToString("D"), InstanceRevision = verification.InstanceRevision }; db.Add(row); }
            verification = verification.Merge(string.IsNullOrEmpty(row.Json) ? null : Read<SandboxInstanceVerification>(row.Json));
            row.Json = JsonSerializer.Serialize(verification, Json); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        finally { _gate.Release(); }
    }
    public async ValueTask<SandboxLease?> GetLeaseAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await TenantSession(db, sessionId, ct)) return null;
        var row = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == null && r.SessionId == sessionId.ToString("D"), ct);
        return row is null || await TenantInstance(db, Guid.Parse(row.AgentInstanceId), ct) ? null : SandboxRuntimePersistence.Value(row);
    }
    public async ValueTask<IReadOnlyList<SandboxLease>> ListLeasesAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return (await db.Set<SandboxLeaseRow>().AsNoTracking().Where(r => r.TenantId == null && r.State != (int)SandboxLeaseState.Terminated
            && !db.Set<TenantSessionRecord>().Any(s => s.SessionId == r.SessionId)
            && !db.Set<IamSystemIdentityRow>().Any(i => i.TenantId != null && i.AgentInstanceId == r.AgentInstanceId)).ToListAsync(ct)).Select(SandboxRuntimePersistence.Value).ToArray();
    }
    public async ValueTask SaveLeaseAsync(SandboxLease lease, long expectedRevision, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (lease.TenantId is not null || lease.ExecutorPrincipalId is not null || lease.Profile.TenantId is not null || await TenantSession(db, lease.SessionId, ct)
                || await TenantInstance(db, lease.AgentInstanceId, ct) || await db.Set<TenantSandboxProfileRecord>().AnyAsync(p => p.ProfileId == lease.Profile.Id, ct))
                throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
            var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(r => r.SessionId == lease.SessionId.ToString("D"), ct);
            if (row?.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
            if ((row?.Revision ?? 0) != expectedRevision || lease.Revision != expectedRevision + 1 || row is not null && row.AgentInstanceId != lease.AgentInstanceId.ToString("D"))
                throw AgentCoreErrors.Conflict("Sandbox generation changed.");
            if (row is null) { row = new() { SessionId = lease.SessionId.ToString("D"), AgentInstanceId = lease.AgentInstanceId.ToString("D") }; db.Add(row); }
            row.Revision = lease.Revision; row.State = (int)lease.State; row.ExpiresAt = lease.ExpiresAt.ToUnixTimeMilliseconds(); row.Json = JsonSerializer.Serialize(lease, Json);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        } finally { _gate.Release(); }
    }
    public async ValueTask<SandboxExecution?> GetExecutionAsync(Guid sessionId, Guid executionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await TenantSession(db, sessionId, ct)) return null;
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == null && r.Id == executionId.ToString("D") && r.SessionId == sessionId.ToString("D"), ct);
        var value = row is null ? null : SandboxRuntimePersistence.Value(row);
        return value is not null && await TenantInstance(db, value.AgentInstanceId, ct) ? null : value;
    }
    public async ValueTask<SandboxExecution?> GetOperationAsync(Guid sessionId, Guid agentRunId, string toolCallId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (await TenantSession(db, sessionId, ct)) return null;
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == null && r.SessionId == sessionId.ToString("D") && r.AgentRunId == agentRunId.ToString("D") && r.ToolCallId == toolCallId, ct);
        var value = row is null ? null : SandboxRuntimePersistence.Value(row);
        return value is not null && await TenantInstance(db, value.AgentInstanceId, ct) ? null : value;
    }
    public async ValueTask SaveExecutionAsync(SandboxExecution execution, bool create, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (execution.ApprovalRequestId is not null || execution.OperationId is not null || execution.TenantId is not null || execution.ExecutorPrincipalId is not null || execution.Revision != 0 || await TenantSession(db, execution.SessionId, ct)
                || await TenantInstance(db, execution.AgentInstanceId, ct)) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
            var row = await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(r => r.Id == execution.Id.ToString("D"), ct);
            if (row?.TenantId is not null) throw new AgentCore.Application.Security.AccessDeniedException("tenant_context_required");
            var old = row is null ? null : Read<SandboxExecution>(row.Json);
            if (create ? old is not null || await db.Set<SandboxExecutionRow>().AnyAsync(r => r.SessionId == execution.SessionId.ToString("D") && r.AgentRunId == execution.AgentRunId.ToString("D") && r.ToolCallId == execution.ToolCallId, ct)
                : old is null || old.SessionId != execution.SessionId || old.Generation != execution.Generation || old.ActionHash != execution.ActionHash)
                throw AgentCoreErrors.Conflict("Sandbox operation receipt changed.");
            if (row is null) { row = new() { Id = execution.Id.ToString("D"), SessionId = execution.SessionId.ToString("D"), AgentRunId = execution.AgentRunId.ToString("D"), ToolCallId = execution.ToolCallId }; db.Add(row); }
            row.Json = JsonSerializer.Serialize(execution, Json); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        } finally { _gate.Release(); }
    }
    private static Task<bool> TenantSession(AgentCoreDbContext db, Guid id, CancellationToken ct) => db.Set<TenantSessionRecord>().AnyAsync(s => s.SessionId == id.ToString("D"), ct);
    private static Task<bool> TenantInstance(AgentCoreDbContext db, Guid id, CancellationToken ct) => db.Set<IamSystemIdentityRow>().AnyAsync(i => i.TenantId != null && i.AgentInstanceId == id.ToString("D"), ct);
}
