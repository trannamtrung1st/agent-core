using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task<AgentRun> RequireCompletionParent(AgentCoreDbContext db, TenantRuntimeFrame frame, string tool, CancellationToken ct)
    {
        var (root, run) = await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        TenantWaitRules.RequireSession(await SessionView(db, root, ct).ConfigureAwait(false)); var tenant = frame.Owner.TenantId!.Value;
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        var agent = Owned(await OwnedAgents(db, tenant).SingleAsync(a => a.Instance.InstanceId == run.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false));
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? (await FindDefinitionPublicationAsync(tenant, definition, agent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        TenantCompletionRules.Require(run, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), tool);
        await RequireTenantExecutionSourcesAsync(db, run, state, new IamAuthorizer(this), this, ct).ConfigureAwait(false); return run;
    }
    private static async ValueTask<AgentRun?> CompletionRun(AgentCoreDbContext db, Guid tenant, Guid id, CancellationToken ct)
    {
        var row = await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.AgentRunId == id.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : AgentRunStoreMapping.ToDomain(row);
    }
    private static async ValueTask<TenantSession?> CompletionSession(AgentCoreDbContext db, Guid tenant, Guid id, CancellationToken ct)
    {
        var row = await OwnedSessions(db, tenant).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false);
        return row is null ? null : await SessionView(db, row, ct).ConfigureAwait(false);
    }
    private async Task<TenantCompletionView?> CompletionView(AgentCoreDbContext db, AgentRun parent, Guid session, DateTimeOffset now, CancellationToken ct)
    {
        var tenant = parent.Owner.TenantId!.Value;
        var source = await TenantWaitRules.ChildAsync(parent, await CompletionSession(db, tenant, session, ct).ConfigureAwait(false),
            (id, token) => CompletionRun(db, tenant, id, token), ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false);
        source = source is null || state is null ? null : await AuthorizeCompletionSource(db, parent, source, ct).ConfigureAwait(false);
        if (source is null) return null;
        var runId = (source.CurrentTaskRun ?? source.Run).AgentRunId.ToString("D");
        var row = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == runId && r.ProfileId == null, ct).ConfigureAwait(false);
        if (row is null) return new(source, null);
        var item = CompletionInboxMapping.Read(row); if (item.Owner != parent.Owner) return null;
        var claimed = item.ClaimRunId is { } claimant ? await CompletionRun(db, tenant, claimant, ct).ConfigureAwait(false) : null;
        item = CompletionInboxMapping.Refresh(item, claimed is null ? [] : [claimed], now); CompletionInboxMapping.Write(row, item);
        return new(source, item);
    }
    public async ValueTask<TenantCompletionPage> ReadTenantCompletionsAsync(TenantRuntimeFrame frame, string tool, Guid? childSessionId,
        Guid? cursor, bool pendingOnly, int limit, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var parent = await RequireCompletionParent(db, frame, tool, ct).ConfigureAwait(false); var now = (clock ?? TimeProvider.System).GetUtcNow();
        var visible = new List<TenantCompletionView>();
        if (childSessionId is { } session)
        {
            if (await CompletionView(db, parent, session, now, ct).ConfigureAwait(false) is { } view) visible.Add(view);
        }
        else
        {
            // Read bounded metadata chunks. Ownership, exact initial-child lineage and current read
            // authorization all precede the page/count projection; no conversation entries are loaded.
            var query = OwnedRuns(db, frame.Owner.TenantId!.Value).AsNoTracking().Where(r => r.AgentInstanceId == frame.Owner.AgentInstanceId.ToString("D")
                && r.ExecutorPrincipalId == frame.Owner.ExecutorPrincipalId).OrderBy(r => r.AgentRunId);
            for (var offset = 0; ; offset += 32)
            {
                var rows = await query.Skip(offset).Take(32).ToArrayAsync(ct).ConfigureAwait(false);
                foreach (var row in rows)
                {
                    var candidate = AgentRunStoreMapping.ToDomain(row);
                    if (candidate.Admission.ExecutionContext?.Origin != ExecutionOrigin.ImmediateBackground || candidate.Admission.Activation.SourceSessionId != frame.SessionId) continue;
                    if (await CompletionView(db, parent, Guid.Parse(row.SessionId), now, ct).ConfigureAwait(false) is { } view) visible.Add(view);
                }
                if (rows.Length < 32) break;
            }
        }
        var page = TenantCompletionRules.Page(visible, childSessionId, cursor, pendingOnly, limit);
        if (childSessionId is not null)
        {
            var source = page.Items[0]; var state = (await ReadTenantAsync(db, frame.Owner.TenantId!.Value, ct).ConfigureAwait(false))!;
            var files = await TenantCompletionRules.ArtifactsAsync(artifactBacking, state, parent, source.Source, new IamAuthorizer(this), ct).ConfigureAwait(false);
            page = page with { Items = [source with { Artifacts = files }] };
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return page;
    }
    public async ValueTask<TenantCompletionView> MutateTenantCompletionAsync(TenantRuntimeFrame frame, Guid childSessionId,
        long expectedRevision, string toolCallId, Guid token, string? usage, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var parent = await RequireCompletionParent(db, frame, usage is null ? ToolCatalog.BackgroundTake : ToolCatalog.BackgroundAcknowledge, ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var view = await CompletionView(db, parent, childSessionId, now, ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Owned readable background task was not found.");
        TenantCompletionRules.RequirePendingMutation(parent, childSessionId, expectedRevision, toolCallId, token, usage);
        var next = TenantCompletionRules.Mutate(view, parent, expectedRevision, toolCallId, token, usage, now);
        var state = (await ReadTenantAsync(db, frame.Owner.TenantId!.Value, ct).ConfigureAwait(false))!;
        var files = await TenantCompletionRules.ArtifactsAsync(artifactBacking, state, parent, view.Source, new IamAuthorizer(this), ct).ConfigureAwait(false);
        var row = await db.BackgroundCompletionReceipts.SingleAsync(r => r.ChildAgentRunId == next.ChildAgentRunId.ToString("D"), ct).ConfigureAwait(false);
        CompletionInboxMapping.Write(row, next);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Completion revision or claim changed."); }
        return view with { Inbox = next, Artifacts = files };
    }
    public async ValueTask<bool> HasTenantCompletionClaimAsync(TenantRuntimeFrame frame, bool acknowledgedOnly, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await RequireRuntimeFrame(db, frame, ct).ConfigureAwait(false);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var rows = await db.BackgroundCompletionReceipts.AsNoTracking().Where(r => r.ParentSessionId == frame.SessionId.ToString("D")
            && r.ClaimRunId == frame.RunId.ToString("D") && r.Status == (int)CompletionInboxStatus.Claimed && r.ClaimExpiresAtUtc > now.ToUnixTimeMilliseconds()).ToArrayAsync(ct).ConfigureAwait(false);
        return rows.Select(CompletionInboxMapping.Read).Any(i => i.Owner == frame.Owner && i.ClaimGeneration == frame.Generation && (!acknowledgedOnly || i.Acknowledgment is not null));
    }
    private async Task RequireTenantOutputSources(AgentCoreDbContext db, AgentRun parent, CancellationToken ct)
    {
        await RequireSpaceResourceReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireContextReadSources(db, parent, ct).ConfigureAwait(false);
        await RequirePublicWebSources(db, parent, ct).ConfigureAwait(false);
        await RequireHttpSources(db, parent, ct).ConfigureAwait(false);
        await RequireStoredProfileSource(db, parent, ct).ConfigureAwait(false);
        await RequireStoredMemorySources(db, parent, ct).ConfigureAwait(false);
        await RequireTenantEventRead(db, parent, ct).ConfigureAwait(false);
        await RequireCredentialReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireAccountReadSources(db, parent, ct).ConfigureAwait(false);
        await RequireAccountActionSources(db, parent, ct).ConfigureAwait(false);
        var tenant = parent.Owner.TenantId!.Value; var state = (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        if (parent.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
        {
            var source = await TenantReportReadRules.SourceAsync(parent, (id, token) => CompletionRun(db, tenant, id, token),
                async (id, token) => await CompletionSource(db, parent.Owner, id, token).ConfigureAwait(false), ct).ConfigureAwait(false);
            await RequireTenantReportRead(db, state, source, ct).ConfigureAwait(false);
            await RequireTenantReportDelivery(db, state, source, parent, ct).ConfigureAwait(false);
        }
        var rows = await db.BackgroundCompletionReceipts.Where(r => r.ClaimRunId == parent.AgentRunId.ToString("D") && r.Status == (int)CompletionInboxStatus.Claimed).ToArrayAsync(ct).ConfigureAwait(false);
        foreach (var item in rows.Select(CompletionInboxMapping.Read).Where(i => i.Owner == parent.Owner && i.Acknowledgment is not null && i.ClaimExpiresAtUtc > now))
            if ((await CompletionView(db, parent, item.ChildSessionId, now, ct).ConfigureAwait(false))?.Inbox?.ChildAgentRunId != item.ChildAgentRunId)
                throw new AccessDeniedException("completion_source_unavailable");
        await TenantCompletionRules.RequireSourcesAsync(parent, state, new IamAuthorizer(this),
            (id, token) => CompletionSession(db, tenant, id, token), (id, token) => CompletionRun(db, tenant, id, token), artifactBacking, ct, (session, source, request, token) => RequireCompletionSourceProof(db, parent, session, source, request, token)).ConfigureAwait(false);
    }
}
