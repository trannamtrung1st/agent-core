using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantRunProfileStore
{
    private async ValueTask<(TenantUser User, TenantAgent Agent, AgentDefinition Current, long SecurityRevision)?> RunProfileReader(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("profile_context_unavailable");
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("profile_context_unavailable");
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false);
        var user = TenantRunProfileRules.User(run, root, state);
        if (user is null || !await db.Set<Account>().AnyAsync(a => a.AccountId == user.AccountId && a.Status == IdentityStatus.Active, ct).ConfigureAwait(false)) return null;
        var session = await ReadSandboxSession(db, tenant, run.SessionId, ct).ConfigureAwait(false);
        var parent = await ReadSandboxSession(db, tenant, root.SessionId, ct).ConfigureAwait(false);
        var row = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false);
        if (session is null || parent is null || row is null) return null; var agent = Owned(row);
        if (!await TenantRunProfileRules.AllowedAsync(run, root, state, session, parent, agent, new IamAuthorizer(this), ct).ConfigureAwait(false)) return null;
        return (user, agent, InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides), state.Tenant.Revision);
    }
    public async ValueTask<TenantRunProfileObservation?> ReadTenantRunProfileAsync(TenantRuntimeFrame frame, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var reader = await RunProfileReader(db, run, ct).ConfigureAwait(false);
        if (reader is null || !TenantRunProfileRules.Eligible(run, reader.Value.Current)) return null;
        var user = reader.Value.User;
        var row = await db.Set<TenantUserProfileRecord>().AsNoTracking().SingleOrDefaultAsync(p => p.TenantId == user.TenantId && p.PrincipalId == user.PrincipalId && p.AccountId == user.AccountId, ct).ConfigureAwait(false);
        var profile = row?.Domain(); return profile is null ? null : new(profile, TenantRunProfileRules.Source(profile), reader.Value.SecurityRevision, reader.Value.Agent.Instance.Revision);
    }
    public async ValueTask RequireTenantProfileProjectionAsync(TenantRuntimeFrame frame, TenantRunProfileObservation observation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var (_, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var reader = await RunProfileReader(db, run, ct).ConfigureAwait(false);
        if (reader is null || !TenantRunProfileRules.Eligible(run, reader.Value.Current) || reader.Value.SecurityRevision != observation.SecurityRevision
            || reader.Value.Agent.Instance.Revision != observation.InstanceRevision || TenantRunProfileRules.Source(observation.Profile) != observation.Source)
            throw new AccessDeniedException("profile_context_changed");
        await RequireProfileSource(db, run, observation.Source, ct).ConfigureAwait(false);
    }
    public async ValueTask RequireTenantProfileSourceAsync(TenantRunReference reference, TenantProfileSource source, Guid? generation, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await OwnedRuns(db, reference.TenantId).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == reference.RunId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("profile_context_unavailable");
        var run = AgentRunStoreMapping.ToDomain(row);
        if (generation is { } expected && (run.Status != AgentRunStatus.Running || run.CancellationRequested || run.Claim?.Generation != expected
            || run.Claim.LeaseExpiresAtUtc <= (clock ?? TimeProvider.System).GetUtcNow()) || generation is null && !run.IsTerminal)
            throw new AccessDeniedException("profile_context_unavailable");
        await RequireProfileSource(db, run, source, ct).ConfigureAwait(false);
    }
    private async ValueTask RequireProfileSource(AgentCoreDbContext db, AgentRun run, TenantProfileSource source, CancellationToken ct)
    {
        TenantRunProfileRules.Validate(source);
        var reader = await RunProfileReader(db, run, ct).ConfigureAwait(false);
        if (reader is null || run.Admission.Configuration is null || run.Admission.Configuration.Definition.StateIsolationPolicy?.ProfileEvidence == false
            || source.TenantId != run.Owner.TenantId || source.PrincipalId != reader.Value.User.PrincipalId || source.AccountId != reader.Value.User.AccountId)
            throw new AccessDeniedException("profile_read_unavailable");
        var row = await db.Set<TenantUserProfileRecord>().AsNoTracking().Where(p => p.TenantId == source.TenantId && p.PrincipalId == source.PrincipalId && p.AccountId == source.AccountId)
            .Select(p => new { p.Revision, p.UpdatedAtUtc }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null || row.Revision != source.Revision || row.UpdatedAtUtc != source.UpdatedAt.ToUnixTimeMilliseconds()) throw new AccessDeniedException("profile_source_changed");
    }
    private async ValueTask RequireStoredProfileSource(AgentCoreDbContext db, AgentRun run, CancellationToken ct)
    {
        if (AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).TenantProfileSource is { } source) await RequireProfileSource(db, run, source, ct).ConfigureAwait(false);
    }
}
