using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantSandboxRuntimeSnapshot?> ReadTenantSandboxRuntimeAsync(Guid tenant, Guid session, Guid? executionId = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await ReadSandboxSession(db, tenant, session, ct).ConfigureAwait(false);
        if (saved is null) return null;
        var row = await db.Set<SandboxLeaseRow>().AsNoTracking().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        var lease = row is null ? null : SandboxRuntimePersistence.Value(row); var id = executionId ?? lease?.ActiveExecutionId ?? lease?.LastExecutionId;
        var execution = id is null ? null : await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == session.ToString("D") && e.Id == id.Value.ToString("D"), ct).ConfigureAwait(false);
        return new(saved.Owner, saved.Snapshot.Revision, lease, execution is null ? null : SandboxRuntimePersistence.Value(execution));
    }
    public async ValueTask<SandboxExecution?> FindTenantSandboxOperationAsync(Guid tenant, Guid session, Guid run, string toolCallId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await ReadSandboxSession(db, tenant, session, ct).ConfigureAwait(false) is null) return null;
        var row = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == session.ToString("D") && e.AgentRunId == run.ToString("D") && e.ToolCallId == toolCallId, ct).ConfigureAwait(false);
        return row is null ? null : SandboxRuntimePersistence.Value(row);
    }
    private static async ValueTask<TenantSession?> ReadSandboxSession(AgentCoreDbContext db, Guid tenant, Guid session, CancellationToken ct)
    {
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == session.ToString("D"), ct).ConfigureAwait(false);
        if (root is null) return null;
        var row = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleOrDefaultAsync(s => s.SessionId == root.SessionId && s.DurablyDeletedAtUtc == null, ct).ConfigureAwait(false);
        return row is null ? null : new(root.Domain(), SqliteMemoryStore.ToSnapshot(row, []));
    }
    private async ValueTask<AgentRun> RequireSandboxReservation(AgentCoreDbContext db, TenantSandboxRuntimeWrite write, SandboxProfile source, DateTimeOffset at, CancellationToken ct)
    {
        var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var session = await ReadSandboxSession(db, tenant, write.Runtime.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var row = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == write.Runtime.RunId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        var parent = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == session.Owner.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var agent = Owned(parent); TenantSessionRules.RequireExecutor(state, agent);
        TenantSandboxRuntimeRules.RequireReservation(write, session, run, agent, state.Tenant.Revision, at);
        if (!await OwnedSandboxProfiles(db, tenant).AnyAsync(p => p.Id == source.Id && p.Revision == source.Revision, ct).ConfigureAwait(false)) throw new AccessDeniedException("resource_unavailable");
        var latest = await OwnedSandboxProfiles(db, tenant).Where(p => p.Id == source.Id).OrderByDescending(p => p.Revision).FirstAsync(ct).ConfigureAwait(false);
        if (!SandboxValue(latest).Enabled) throw new AccessDeniedException("resource_unavailable");
        if (source.RegistryCredentialId is { } credential)
        {
            var value = await OwnedCredentials(db, tenant).SingleOrDefaultAsync(c => c.CredentialId == credential.ToString("D"), ct).ConfigureAwait(false);
            TenantSandboxProfileRules.RequireCredential(source, value is null ? null : SqliteCredentialStore.Map(value), at);
        }
        await TenantSandboxRuntimeRules.RequirePermissionsAsync(state, run, source, new IamAuthorizer(this), ct).ConfigureAwait(false);
        return run;
    }
    public async ValueTask<bool> TryReserveTenantSandboxLeaseAsync(TenantSandboxRuntimeWrite write, SandboxLease lease, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var run = await RequireSandboxReservation(db, write, lease.Profile, at, ct).ConfigureAwait(false);
        var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.SessionId == lease.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if ((row?.Revision ?? 0) != expectedRevision || row is not null && row.TenantId != write.Runtime.Owner.TenantId) return false;
        if (await db.Set<SandboxExecutionRow>().AnyAsync(e => e.SessionId == lease.SessionId.ToString("D") && e.AgentRunId == write.Runtime.RunId.ToString("D")
            && e.ToolCallId == lease.AdmissionToolCallId, ct).ConfigureAwait(false)) return false;
        TenantSandboxRuntimeRules.ValidateNewLease(write, lease, expectedRevision, run, row is null ? null : SandboxRuntimePersistence.Value(row), at);
        var next = SandboxRuntimePersistence.Row(lease); if (row is null) db.Add(next); else db.Entry(row).CurrentValues.SetValues(next);
        return await SaveSandbox(db, tx, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TryReserveTenantSandboxExecutionAsync(TenantSandboxRuntimeWrite write, SandboxExecution execution, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = write.Runtime.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == execution.SessionId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var lease = SandboxRuntimePersistence.Value(row); var run = await RequireSandboxReservation(db, write, lease.Profile, at, ct).ConfigureAwait(false);
        var previous = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.SessionId == execution.SessionId.ToString("D") && e.AgentRunId == execution.AgentRunId.ToString("D") && e.ToolCallId == execution.ToolCallId, ct).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.TenantId != tenant || SandboxRuntimePersistence.Value(previous).ActionHash != execution.ActionHash) throw new SecurityConflictException("operation_conflict");
            return false;
        }
        TenantSandboxRuntimeRules.ValidateNewExecution(write, execution, lease, run, at);
        db.Add(SandboxRuntimePersistence.Row(execution));
        var next = lease with { Revision = checked(lease.Revision + 1), State = SandboxLeaseState.Executing, ActiveExecutionId = execution.Id,
            LastExecutionId = execution.Id, AdmissionRunId = execution.AgentRunId, AdmissionToolCallId = execution.ToolCallId, LastActivityAt = at };
        db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(next));
        return await SaveSandbox(db, tx, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TrySaveTenantSandboxLeaseFactsAsync(Guid tenant, SandboxLease lease, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenant && s.SessionId == lease.SessionId.ToString("D"), ct).ConfigureAwait(false);
        var row = await db.Set<SandboxLeaseRow>().SingleOrDefaultAsync(l => l.TenantId == tenant && l.SessionId == lease.SessionId.ToString("D"), ct).ConfigureAwait(false);
        if (root?.ExecutorPrincipalId != lease.ExecutorPrincipalId || row?.Revision != expectedRevision) return false;
        var old = SandboxRuntimePersistence.Value(row); TenantSandboxRuntimeRules.ValidateLeaseFacts(old, lease);
        if (lease.ActiveExecutionId is { } active)
        {
            var record = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == row.SessionId && e.Id == active.ToString("D"), ct).ConfigureAwait(false);
            if (record is null || SandboxRuntimePersistence.Value(record).Generation != lease.Generation) throw new ArgumentException("Execution does not belong to this generation.");
        }
        if (old.ActiveExecutionId is { } previous && lease.ActiveExecutionId != previous && lease.State != SandboxLeaseState.Terminated)
        {
            var record = await db.Set<SandboxExecutionRow>().AsNoTracking().SingleAsync(e => e.TenantId == tenant && e.SessionId == row.SessionId && e.Id == previous.ToString("D"), ct).ConfigureAwait(false);
            if (!SandboxRuntimePersistence.Value(record).TerminationConfirmed) throw new SecurityConflictException("sandbox_recovery_required");
        }
        db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(lease)); return await SaveSandbox(db, tx, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> TrySaveTenantSandboxExecutionFactsAsync(Guid tenant, SandboxExecution execution, long expectedRevision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.Set<SandboxExecutionRow>().SingleOrDefaultAsync(e => e.TenantId == tenant && e.SessionId == execution.SessionId.ToString("D") && e.Id == execution.Id.ToString("D"), ct).ConfigureAwait(false);
        if (row?.Revision != expectedRevision || row.ExecutorPrincipalId != execution.ExecutorPrincipalId) return false;
        TenantSandboxRuntimeRules.ValidateExecutionFacts(SandboxRuntimePersistence.Value(row), execution);
        db.Entry(row).CurrentValues.SetValues(SandboxRuntimePersistence.Row(execution)); return await SaveSandbox(db, tx, ct).ConfigureAwait(false);
    }
    private static async ValueTask<bool> SaveSandbox(AgentCoreDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { return false; }
    }
}
