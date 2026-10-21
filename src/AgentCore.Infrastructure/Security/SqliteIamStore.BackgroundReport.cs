using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async ValueTask<TenantBackgroundCompletion> RequireBackgroundReportOperation(AgentCoreDbContext db, TenantApprovalRequest request, AgentRun child, TenantAgent agent, CancellationToken ct)
    {
        var source = await CompletionSource(db, child.Owner, child.AgentRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("completion_unavailable");
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!; var iam = new IamAuthorizer(this);
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        TenantApprovalRules.Validate(request, child, agent); TenantSessionRules.RequireExecutor(state, agent);
        await RequireTenantReportRead(db, state, source, ct).ConfigureAwait(false);
        TenantBackgroundReportRules.Require(request, source, agent, current);
        await TenantExecutionLineage.RequireRequesterAsync(child, state, iam, this, ct).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(child, this, ct).ConfigureAwait(false);
        await TenantBackgroundReportRules.ExecutorAsync(state, request, root, current, iam, ct, source).ConfigureAwait(false);
        return source;
    }
    public async ValueTask<bool> SettleTenantBackgroundReportApprovalAsync(ApprovalProcessedSignal signal, AgentRun? report, string? refusal, Guid auditId, CancellationToken ct = default)
    {
        if (auditId == Guid.Empty || refusal is { Length: > 80 } || report is not null && refusal is not null) throw new ArgumentException("Invalid report settlement.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct).ConfigureAwait(false);
        var requestRow = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct).ConfigureAwait(false);
        if (saved != signal || saved?.ProcessedAt is not null || requestRow is null) return false;
        var request = requestRow.Domain(); var p = request.Operation.BackgroundReport;
        if (p is null || request.Status == TenantApprovalStatus.Pending || request.Revision != signal.DecisionRevision || request.RunId != signal.RunId) return false;
        var now = ApprovalNow; if (now < signal.CreatedAt) return false;
        var state = await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false); if (state is null) return false;
        var row = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == request.RunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false); if (row is null) return false;
        var item = CompletionInboxMapping.Read(row);
        if (item.Owner != AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId)) throw new SecurityConflictException("operation_mismatch");
        var source = await CompletionSource(db, item.Owner, request.RunId, ct).ConfigureAwait(false);
        var agentRow = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var agent = agentRow is null ? null : Owned(agentRow); var approved = request.Status == TenantApprovalStatus.Approved;
        var reason = approved ? refusal : TenantApprovalRules.DuplicateReason(request);
        var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        if (approved && source?.Parent is { } pendingParent && (pendingParent.Snapshot.PendingAgentInputIds.Count > 0
            || await db.AgentRuns.AnyAsync(r => r.SessionId == p.ParentSessionId.ToString("D") && r.Status != (int)AgentRunStatus.Completed
                && r.Status != (int)AgentRunStatus.Failed && r.Status != (int)AgentRunStatus.Cancelled, ct).ConfigureAwait(false))) return false;
        if (approved)
        {
            try
            {
                var decision = await db.Set<TenantApprovalDecision>().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
                TenantBackgroundReportRules.Decision(request, decision, grant, now);
                if (source is null || agent is null) throw new AccessDeniedException("completion_unavailable");
                source = await RequireBackgroundReportOperation(db, request, source.Child, agent, ct).ConfigureAwait(false);
                await TenantBackgroundReportRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
                if (report is not null)
                {
                    TenantBackgroundReportRules.Report(request, source, report, agent, now);
                    if (source.Parent!.Snapshot.PendingAgentInputIds.Count > 0 || await db.AgentRuns.AnyAsync(r => r.SessionId == p.ParentSessionId.ToString("D")
                        && r.Status != (int)AgentRunStatus.Completed && r.Status != (int)AgentRunStatus.Failed && r.Status != (int)AgentRunStatus.Cancelled, ct).ConfigureAwait(false)) return false;
                }
                else if (string.IsNullOrWhiteSpace(reason)) throw new SecurityConflictException("operation_mismatch");
            }
            catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) when (error.ReasonCode != "operation_mismatch") { reason = error.ReasonCode; }
        }
        else if (report is not null) throw new SecurityConflictException("operation_mismatch");
        var complete = approved && reason is null && report is not null;
        if (complete)
        {
            db.Activations.Add(AgentRunStoreMapping.ToActivationRecord(source!.Parent!.Snapshot, report!)); db.AgentRuns.Add(AgentRunStoreMapping.ToRecord(report!));
        }
        if (item.Status == CompletionInboxStatus.Pending)
            CompletionInboxMapping.Write(row, item with { Revision = item.Revision + 1, Status = complete ? CompletionInboxStatus.DeliveryQueued : CompletionInboxStatus.Skipped,
                ReportActivationId = complete ? report!.ActivationId : null, SkipReason = complete ? null : reason });
        if (grant is not null) db.Entry(grant).CurrentValues.SetValues(grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = grant.Revision + 1 });
        var code = complete ? "completion_report_created" : reason ?? "completion_unavailable";
        if (approved) db.Add(TenantOperationReceiptRecord.From(new(request.Operation.OperationId, request.RequestId, request.TenantId, request.RunId, request.SessionId,
            request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash, complete ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied,
            code, agent?.Instance.Revision ?? request.Operation.ExpectedInstanceRevision, agent?.Instance.PersonaRevision ?? request.Operation.ExpectedPersonaRevision, now)));
        db.Entry(saved!).CurrentValues.SetValues(signal with { Revision = signal.Revision + 1, ProcessedAt = now });
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == request.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(state.Tenant with { Revision = checked(state.Tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, request.TenantId, request.ExecutorPrincipalId, "agent-runs.background.report", request.ResourceId, complete ? "allowed" : "denied", code, now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { throw new SecurityConflictException("completion_conflict"); }
    }
}
