using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<TenantBackgroundCompletion?> ReadTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(owner); await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await CompletionSource(db, owner, childRunId, ct).ConfigureAwait(false);
    }
    private async Task<TenantBackgroundCompletion?> CompletionSource(AgentCoreDbContext db, AgentRunOwner owner, Guid childRunId, CancellationToken ct)
    {
        var row = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == owner.TenantId && r.AgentRunId == childRunId.ToString("D")
            && r.AgentInstanceId == owner.AgentInstanceId.ToString("D") && r.ExecutorPrincipalId == owner.ExecutorPrincipalId, ct).ConfigureAwait(false);
        if (row is null) return null; var run = AgentRunStoreMapping.ToDomain(row);
        var root = await db.Set<TenantSessionRecord>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == owner.TenantId && s.SessionId == row.SessionId
            && s.ExecutorPrincipalId == owner.ExecutorPrincipalId && s.AgentInstanceId == row.AgentInstanceId, ct).ConfigureAwait(false);
        if (root is null) return null;
        var childRow = await db.Sessions.AsNoTracking().Include(s => s.Snapshot).SingleAsync(s => s.SessionId == row.SessionId, ct).ConfigureAwait(false);
        var child = new TenantSession(root.Domain(), SqliteMemoryStore.ToSnapshot(childRow, []));
        var parentRoot = child.Snapshot.Origin.OriginatingSessionId is { } id
            ? await OwnedSessions(db, owner.TenantId!.Value).AsNoTracking().SingleOrDefaultAsync(s => s.SessionId == id.ToString("D"), ct).ConfigureAwait(false) : null;
        var parent = parentRoot is null ? null : await SessionView(db, parentRoot, ct).ConfigureAwait(false);
        var initial = await TenantBackgroundLineage.InitialAsync(run, async (id, token) => {
            var previous = await db.AgentRuns.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == owner.TenantId && r.AgentRunId == id.ToString("D"), token).ConfigureAwait(false);
            return previous is null ? null : AgentRunStoreMapping.ToDomain(previous);
        }, ct).ConfigureAwait(false);
        var receipt = await db.BackgroundCompletionReceipts.AsNoTracking().SingleOrDefaultAsync(r => r.ChildAgentRunId == childRunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false);
        var occurrenceRow = initial.Admission.Activation.TriggerOccurrenceId is { } occurrenceId
            ? await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == owner.TenantId && o.OccurrenceId == occurrenceId.ToString("D"), ct).ConfigureAwait(false) : null;
        return new(run, child, parent, initial, receipt is null ? null : CompletionInboxMapping.Read(receipt), occurrenceRow is null ? null : TriggerStoreMapping.ToOccurrence(occurrenceRow));
    }
    public async ValueTask<IReadOnlyList<TenantBackgroundCompletion>> ListTenantBackgroundCompletionsAsync(int limit, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var blocked = db.Set<TenantApprovalRecord>().FromSqlRaw("""
            SELECT * FROM TenantApprovalRequests WHERE json_type(PayloadJson, '$.Operation.BackgroundReport') IS 'object'
              OR (json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object' AND (Status <> 1 OR NOT EXISTS (
                SELECT 1 FROM TenantOperationReceipts receipt WHERE receipt.TenantId = TenantApprovalRequests.TenantId
                AND receipt.OperationId = TenantApprovalRequests.OperationId AND receipt.RequestId = TenantApprovalRequests.RequestId
                AND receipt.Outcome = 0 AND receipt.ReasonCode = 'report_source_read_authorized')))
            """);
        var rows = await db.AgentRuns.AsNoTracking().Where(r => r.TenantId != null && !blocked.Any(a => a.TenantId == r.TenantId && a.ExecutorPrincipalId == r.ExecutorPrincipalId && a.RunId == r.AgentRunId && a.RootActivationId == r.RootActivationId) && db.BackgroundCompletionReceipts.Any(i => i.ChildAgentRunId == r.AgentRunId && i.ProfileId == null && i.Status == (int)CompletionInboxStatus.Pending
                && !db.AgentRuns.Any(parent => parent.SessionId == i.ParentSessionId && parent.Status != (int)AgentRunStatus.Completed && parent.Status != (int)AgentRunStatus.Failed && parent.Status != (int)AgentRunStatus.Cancelled)
                && !db.Sessions.Any(parent => parent.SessionId == i.ParentSessionId && parent.PendingAgentInputIdsJson != "[]")))
            .OrderBy(r => r.UpdatedAtUtc).ThenBy(r => r.AgentRunId).Take(limit).ToArrayAsync(ct).ConfigureAwait(false);
        var values = new List<TenantBackgroundCompletion>();
        foreach (var row in rows)
            if (await CompletionSource(db, AgentRunStoreMapping.ToDomain(row).Owner, Guid.Parse(row.AgentRunId), ct).ConfigureAwait(false) is { } source) values.Add(source);
        return values;
    }
    public async ValueTask SkipTenantBackgroundCompletionAsync(AgentRunOwner owner, Guid childRunId, string reason, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(owner); if (string.IsNullOrWhiteSpace(reason) || reason.Length > 80) throw new ArgumentException("Use a bounded reason code.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (await CompletionSource(db, owner, childRunId, ct).ConfigureAwait(false) is null) return;
        var row = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == childRunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false);
        if (row is null) return; var item = CompletionInboxMapping.Read(row);
        if (item.Owner != owner || item.Status != CompletionInboxStatus.Pending) return;
        CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = reason });
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); } catch (DbUpdateConcurrencyException) { }
    }
    public async ValueTask<AgentRunAdmissionResult> AdmitTenantBackgroundCompletionAsync(AgentRun report, long expectedParentRevision, Guid auditId, CancellationToken ct = default)
    {
        TenantRuntimeRules.RequireOwner(report.Owner); if (auditId == Guid.Empty) throw new ArgumentException("Audit identity is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tenant = report.Owner.TenantId!.Value;
        var source = await CompletionSource(db, report.Owner, report.Admission.ExecutionContext?.ParentRunId ?? Guid.Empty, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        await RequireTenantReportRead(db, (await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false))!, source, ct).ConfigureAwait(false);
        var receipt = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == source.Child.AgentRunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        var item = CompletionInboxMapping.Read(receipt); if (item.Owner != report.Owner) throw new AccessDeniedException("completion_unavailable");
        if (item.Status != CompletionInboxStatus.Pending)
        {
            var previous = item.ReportActivationId is { } activation ? await OwnedRuns(db, tenant).AsNoTracking().SingleOrDefaultAsync(r => r.ActivationId == activation.ToString("D"), ct).ConfigureAwait(false) : null;
            if (previous is not null) return new(false, AgentRunStoreMapping.ToDomain(previous));
            throw new AccessDeniedException("completion_unavailable");
        }
        if (await db.Set<TenantApprovalRecord>().FromSqlInterpolated($"""
            SELECT * FROM TenantApprovalRequests WHERE json_extract(PayloadJson, '$.Operation.BackgroundReport.ChildRunId') = {source.Child.AgentRunId.ToString("D")}
            """).AnyAsync(r => r.TenantId == tenant, ct).ConfigureAwait(false)) throw new AccessDeniedException("report_approval_required");
        var state = await ReadTenantAsync(db, tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        AgentCore.Application.Models.ExecutionModelPolicy.RequireCurrentDecision(report.PinnedModel,
            await db.Set<TenantModelSettingsRecord>().Where(settings => settings.TenantId == tenant).Select(settings => (long?)settings.Revision).SingleOrDefaultAsync(ct).ConfigureAwait(false) ?? 0);
        var agentRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == report.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(agentRow); TenantSessionRules.RequireExecutor(state, agent); var now = (clock ?? TimeProvider.System).GetUtcNow();
        TenantBackgroundCompletionRules.Shape(source, report, agent, expectedParentRevision, now);
        await RequireSpaceExecutionSource(db, report, state, new IamAuthorizer(this), ct, admission: true).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(source.Child, this, ct).ConfigureAwait(false);
        await TenantCompletionDestinationRules.CommunicationAsync(state, source, new IamAuthorizer(this), ct, requireNoReview: true).ConfigureAwait(false);
        var baseline = Guid.TryParse(agent.Instance.DefinitionId, out var definition)
            ? (await FindDefinitionPublicationAsync(tenant, definition, agent.Instance.ActiveVersion, ct).ConfigureAwait(false))?.Payload
            : builtIns is null ? null : await builtIns.GetAsync(agent.Instance.DefinitionId, agent.Instance.ActiveVersion, ct).ConfigureAwait(false);
        if (baseline is null) throw new AccessDeniedException("definition_unavailable");
        await TenantBackgroundAdmissionRules.AuthorizeReportAsync(state, root, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides), new IamAuthorizer(this), this, ct, source).ConfigureAwait(false);
        if (source.Parent!.Snapshot.PendingAgentInputIds.Count > 0 || await db.AgentRuns.AnyAsync(r => r.SessionId == report.SessionId.ToString("D") && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Cancelled && r.Status != (int)AgentRunStatus.Failed, ct).ConfigureAwait(false))
            throw new SecurityConflictException("parent_busy");
        db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(source.Parent.Snapshot, report)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(report));
        CompletionInboxMapping.Write(receipt, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.DeliveryQueued, ReportActivationId = report.ActivationId });
        var tenantRow = await db.Set<Tenant>().SingleAsync(t => t.TenantId == tenant, ct).ConfigureAwait(false);
        db.Entry(tenantRow).CurrentValues.SetValues(state.Tenant with { Revision = checked(state.Tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, tenant, report.Owner.ExecutorPrincipalId!.Value, "agent-runs.background.report", report.AgentRunId.ToString("D"), "allowed", "owned_completion_admitted", now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return new(true, report); }
        catch (DbUpdateConcurrencyException) { throw new SecurityConflictException("stale_revision"); }
        catch (DbUpdateException ex) when (IsConstraint(ex)) { throw new SecurityConflictException("completion_conflict"); }
    }
}
