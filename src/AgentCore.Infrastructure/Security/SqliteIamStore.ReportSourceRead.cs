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
    private async ValueTask<TenantBackgroundCompletion> RequireReportSourceReadOperation(AgentCoreDbContext db, TenantApprovalRequest request, CancellationToken ct, bool pending = false)
    {
        var source = await CompletionSource(db, AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId), request.RunId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_unavailable");
        var state = (await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false))!; var iam = new IamAuthorizer(this);
        var row = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(row); TenantSessionRules.RequireExecutor(state, agent);
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        await TenantExecutionLineage.RequireRequesterAsync(source.Child, state, iam, this, ct).ConfigureAwait(false);
        var root = await TenantExecutionLineage.RootAsync(source.Child, this, ct).ConfigureAwait(false);
        TenantReportSourceReadRules.Require(request, source, agent, current, root, pending);
        await TenantReportReadRules.RequireRawAsync(state, source, iam, ct).ConfigureAwait(false);
        return source;
    }
    private async ValueTask RequireTenantReportRead(AgentCoreDbContext db, TenantSecurityState state, TenantBackgroundCompletion source, CancellationToken ct)
    {
        TenantBackgroundCompletionFactory.RequireParent(source);
        var iam = new IamAuthorizer(this);
        await TenantReportReadRules.RequireRawAsync(state, source, iam, ct).ConfigureAwait(false);
        var requestRow = await RootApproval(db, state.Tenant.TenantId, source.Child.Admission.ExecutionContext!.RootActivationId.ToString("D"),
            source.Child.Owner.ExecutorPrincipalId!.Value, TenantReportSourceReadRules.Lookup(source)).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (requestRow is null) { await TenantReportReadRules.RequireAsync(state, source, iam, ct).ConfigureAwait(false); return; }
        var request = requestRow.Domain();
        var receiptRow = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.OperationId == request.Operation.OperationId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_read_review_required");
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_read_review_required");
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantReportSourceReadRules.Receipt(request, receiptRow.Domain(), decision, grant);
        await RequireReportSourceReadOperation(db, request, ct).ConfigureAwait(false);
        await TenantReportSourceReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
    private async Task RequireTenantReportDelivery(AgentCoreDbContext db, TenantSecurityState state, TenantBackgroundCompletion source, AgentRun report, CancellationToken ct)
    {
        var iam = new IamAuthorizer(this);
        var agentRow = await OwnedAgents(db, state.Tenant.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == source.Child.AgentInstanceId.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(agentRow);
        var root = await TenantExecutionLineage.RootAsync(source.Child, this, ct).ConfigureAwait(false);
        var current = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct).ConfigureAwait(false), agent.Instance.SettingsOverrides);
        if (!TenantCompletionDestinationRules.Allows(source, root, current)) throw new AccessDeniedException("background_unavailable");
        if (!await TenantCompletionDestinationRules.NeedsCommunicationReviewAsync(state, source, iam, ct).ConfigureAwait(false)) return;
        var row = await RootApproval(db, state.Tenant.TenantId, source.Child.Admission.ExecutionContext!.RootActivationId.ToString("D"),
            source.Child.Owner.ExecutorPrincipalId!.Value, TenantBackgroundReportRules.Lookup(source)).AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        var request = row.Domain();
        var receipt = await db.Set<TenantOperationReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == request.TenantId && r.OperationId == request.Operation.OperationId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("completion_communication_review_required");
        var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
        TenantBackgroundReportRules.DeliveryReceipt(source, report, request, receipt.Domain(), decision, grant);
        TenantApprovalRules.Validate(request, source.Child, agent);
        await TenantBackgroundReportRules.ReviewerAsync(state, request, decision.HumanPrincipalId, iam, ct).ConfigureAwait(false);
    }
    public async ValueTask<bool> SettleTenantReportSourceReadAsync(ApprovalProcessedSignal signal, Guid auditId, CancellationToken ct = default)
    {
        if (auditId == Guid.Empty) throw new ArgumentException("Audit identity is required.");
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false); await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var saved = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct).ConfigureAwait(false);
        var row = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct).ConfigureAwait(false);
        if (saved != signal || saved?.ProcessedAt is not null || row is null) return false;
        var request = row.Domain();
        if (request.Operation.ReportSourceRead is null || request.Status == TenantApprovalStatus.Pending || request.Revision != signal.DecisionRevision || request.RunId != signal.RunId) return false;
        var now = ApprovalNow; if (now < signal.CreatedAt) return false;
        var state = await ReadTenantAsync(db, request.TenantId, ct).ConfigureAwait(false); if (state is null) return false;
        var approved = request.Status == TenantApprovalStatus.Approved; string? reason = approved ? null : TenantApprovalRules.DuplicateReason(request);
        if (approved)
        {
            try
            {
                var decision = await db.Set<TenantApprovalDecision>().AsNoTracking().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct).ConfigureAwait(false)
                    ?? throw new AccessDeniedException("approval_unavailable");
                var grant = await db.Set<TenantOperationGrant>().AsNoTracking().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct).ConfigureAwait(false);
                TenantBackgroundReportRules.Decision(request, decision, grant, now);
                await RequireReportSourceReadOperation(db, request, ct, pending: true).ConfigureAwait(false);
                await TenantReportSourceReadRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).ConfigureAwait(false);
            }
            catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) { reason = error.ReasonCode; }
        }
        var code = reason ?? TenantReportSourceReadRules.Reason;
        if (reason is not null)
        {
            var itemRow = await db.BackgroundCompletionReceipts.SingleOrDefaultAsync(r => r.ChildAgentRunId == request.RunId.ToString("D") && r.ProfileId == null, ct).ConfigureAwait(false);
            if (itemRow is not null)
            {
                var item = CompletionInboxMapping.Read(itemRow);
                if (item.Owner == AgentRunOwner.ForTenant(request.TenantId, request.Operation.InstanceId, request.ExecutorPrincipalId) && item.Status == CompletionInboxStatus.Pending)
                    CompletionInboxMapping.Write(itemRow, item with { Revision = item.Revision + 1, Status = CompletionInboxStatus.Skipped, SkipReason = code });
            }
        }
        if (approved) db.Add(TenantOperationReceiptRecord.From(new(request.Operation.OperationId, request.RequestId, request.TenantId, request.RunId, request.SessionId,
            request.RootActivationId, request.ExecutorPrincipalId, request.Operation.InstanceId, request.Operation.ActionHash,
            reason is null ? TenantOperationOutcome.Completed : TenantOperationOutcome.Denied, code, request.Operation.ExpectedInstanceRevision, request.Operation.ExpectedPersonaRevision, now)));
        db.Entry(saved!).CurrentValues.SetValues(signal with { Revision = signal.Revision + 1, ProcessedAt = now });
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == request.TenantId, ct).ConfigureAwait(false);
        db.Entry(tenant).CurrentValues.SetValues(state.Tenant with { Revision = checked(state.Tenant.Revision + 1) });
        db.Add(new AuthorizationAudit(auditId, request.TenantId, request.ExecutorPrincipalId, TenantReportSourceReadRules.AuditAction, request.ResourceId, reason is null ? "allowed" : "denied", code, now));
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
    }
}
