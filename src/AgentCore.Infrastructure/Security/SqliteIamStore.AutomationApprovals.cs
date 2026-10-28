using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<bool> IsTenantAutomationBusyAsync(Guid tenant, Guid automation, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return await TenantAutomationBusy(db, tenant, automation, ct); }

    public async ValueTask<TenantAutomationAdmissionResult?> TryPrepareTenantAutomationApprovalAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantAutomationAdmission admission, TenantApprovalRequest request, HumanSessionReference? human, AuthorizationAudit audit, CancellationToken ct = default)
    {
        next = IamStateRules.Freeze(next); ValidateAutomationPreparation(next, expectedSecurityRevision, request, audit);
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var tenant = await db.Set<Tenant>().SingleOrDefaultAsync(t => t.TenantId == request.TenantId, ct);
        if (tenant?.Revision != expectedSecurityRevision) return null;
        var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Exact Automation consent source required.");
        if (human is not null) await RequireHumanSessionAsync(db, request.TenantId, human, ct);
        if (human?.PrincipalId != p.ManualInitiatorPrincipalId) throw new AccessDeniedException("initiator_unavailable");
        var row = await OwnedAutomations(db, request.TenantId).SingleOrDefaultAsync(a => a.AutomationId == p.Current.AutomationId.ToString("D"), ct);
        if (row is null || !TenantAutomationRules.Same(TriggerStoreMapping.ToRegistration(row), p.Current)) return null;
        if (request.ExpiresAt <= ApprovalNow) throw new SecurityConflictException("approval_expired");
        var agentRow = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(agentRow); var state = (await ReadTenantAsync(db, request.TenantId, ct))!;
        var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
        TenantAutomationAdmissionApprovalRules.Shape(request, agent);
        TenantAutomationRules.Admission(admission, agent, human?.PrincipalId, p);
        var decision = await TenantAutomationAdmissionApprovalRules.EvaluateAsync(state, p.Current, agent, policy, human?.PrincipalId, new IamAuthorizer(this), ct, p.Occurrence.SourceKind, p.EventSource, p.EmailSourceAccountIds);
        if (decision.Kind != request.Kind || decision.Permission != request.Permission) throw new SecurityConflictException("operation_authority_changed");
        if (human is not null) await RequireAutomationHuman(state, human.PrincipalId, agent, p.Current.AutomationId, "automations.run", false, ct);
        if (await TenantAutomationBusy(db, request.TenantId, p.Current.AutomationId, ct)) throw new SecurityConflictException("automation_in_use");
        var recent = db.Set<TenantApprovalRecord>().Where(r => r.TenantId == request.TenantId && (r.Status == (int)TenantApprovalStatus.Pending
            || r.CreatedAt >= (ApprovalNow - TenantApprovalLimits.Window).ToUnixTimeMilliseconds()));
        TenantApprovalLimits.Require(await recent.CountAsync(ct), await recent.CountAsync(r => r.ExecutorPrincipalId == request.ExecutorPrincipalId, ct),
            await recent.CountAsync(r => r.OriginalInitiatorPrincipalId == request.OriginalInitiatorPrincipalId, ct),
            await recent.CountAsync(r => r.Fingerprint == request.Operation.ActionHash, ct),
            await db.Set<TenantApprovalRecord>().CountAsync(r => r.TenantId == request.TenantId && r.RootActivationId == request.RootActivationId.ToString("D"), ct), request.OriginalInitiatorPrincipalId is not null);
        var qualified = false;
        foreach (var user in state.Users)
        {
            try { await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, user.PrincipalId, new IamAuthorizer(this), ct); qualified = true; break; }
            catch (AccessDeniedException) { }
        }
        if (!qualified) throw new AccessDeniedException("no_eligible_approver");
        await RequireAutomationConversationAdmission(db, state, admission, human?.PrincipalId, p, true, ct);
        await RequireTenantEventAdmission(db, admission, p, false, ct);
        var occurrence = TenantAutomationAdmissionApprovalRules.Pending(p);
        try
        {
            StageAutomation(db, row, p.Next); db.TriggerOccurrences.Add(TriggerStoreMapping.ToRecord(occurrence));
            // The request trigger proves the actual pending source. Both saves
            // belong to this transaction; no provisional work supplies an FK.
            await db.SaveChangesAsync(ct);
            if (p.EventSource is not null)
            {
                await SettleTenantEventSource(db, p.EventSource, p.Occurrence.OccurrenceId, true, null, ct);
                await db.SaveChangesAsync(ct);
            }
            db.Add(TenantApprovalRecord.From(request));
            db.Entry(tenant).CurrentValues.SetValues(next.Tenant); await ApplyTenantChildrenAsync(db, next, ct); db.Add(audit);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(true, occurrence, null, null, request.RequestId);
        }
        catch (DbUpdateConcurrencyException) { return null; }
        catch (DbUpdateException error) when (IsConstraint(error)) { throw new SecurityConflictException("automation_admission_conflict"); }
    }

    private static void ValidateAutomationPreparation(TenantSecurityState next, long expected, TenantApprovalRequest request, AuthorizationAudit audit)
    {
        if (next.Tenant.TenantId != request.TenantId || next.Tenant.Revision != expected + 1 || request.Status != TenantApprovalStatus.Pending || request.Revision != 1
            || audit.TenantId != request.TenantId || audit.ActorPrincipalId != request.ExecutorPrincipalId || audit.Action != "approvals.request"
            || audit.Target != request.RequestId.ToString("D") || audit.At != request.CreatedAt || audit.Decision != "allowed") throw new ArgumentException("Invalid occurrence consent transaction.");
    }
    private static Task<bool> TenantAutomationBusy(AgentCoreDbContext db, Guid tenant, Guid automation, CancellationToken ct) => db.TriggerOccurrences.IgnoreQueryFilters().AnyAsync(o =>
        o.TenantId == tenant && o.AutomationId == automation.ToString("D") && (o.Disposition == (int)OccurrenceRoutingDisposition.AwaitingDurableWork
            && o.DispositionReason == TenantAutomationAdmissionApprovalRules.PendingReason || db.Set<AgentCore.Infrastructure.Persistence.WorkflowExecutionRow>().Any(e => e.TenantId == tenant && e.ExecutionId == o.AcceptedWorkflowExecutionId && e.Status < 8) || db.AgentRuns.Any(r => r.AgentRunId == o.AcceptedAgentRunId
                && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Completed && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Failed
                && r.Status != (int)AgentCore.Domain.Conversation.AgentRunStatus.Cancelled)), ct);

    private async ValueTask<TenantAgent> RequireAutomationAdmissionOperation(AgentCoreDbContext db, TenantApprovalRequest request, TenantSecurityState state, CancellationToken ct)
    {
        var p = request.Operation.AutomationAdmission!;
        var row = await OwnedAutomations(db, request.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == p.Current.AutomationId.ToString("D"), ct)
            ?? throw new AccessDeniedException("automation_unavailable");
        var occurrence = await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == request.TenantId && o.OccurrenceId == p.Occurrence.OccurrenceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("occurrence_unavailable");
        if (!TenantAutomationRules.Same(TriggerStoreMapping.ToRegistration(row), p.Next)
            || !TenantAutomationAdmissionApprovalRules.SameOccurrence(TriggerStoreMapping.ToOccurrence(occurrence), TenantAutomationAdmissionApprovalRules.Pending(p)))
            throw new SecurityConflictException("operation_witness_changed");
        var agentRow = await OwnedAgents(db, request.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == request.Operation.InstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("executor_unavailable");
        var agent = Owned(agentRow); TenantAutomationAdmissionApprovalRules.Shape(request, agent);
        var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
        try { TenantAutomationRules.AdmissionSource(state, p.Current, agent, policy, p.ManualInitiatorPrincipalId, p.Occurrence.SourceKind); }
        catch (ArgumentException) { throw new AccessDeniedException("operation_configuration_changed"); }
        if (p.EventSource is { } eventSource) await RequireTenantEventSource(db, p.Current, p.Occurrence, eventSource, true, ct);
        await RequireAutomationConversation(db, state, p.Current, p.ManualInitiatorPrincipalId, p.Conversation, ct);
        await TenantAutomationAdmissionApprovalRules.ExecutorAsync(state, request, new IamAuthorizer(this), ct); return agent;
    }

    public async ValueTask<bool> TrySettleTenantAutomationApprovalAsync(long expectedSecurityRevision, ApprovalProcessedSignal signal,
        TenantAutomationAdmission? admission, string? refusal, Guid auditId, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); at = ApprovalNow;
        var saved = await db.Set<ApprovalProcessedSignal>().SingleOrDefaultAsync(s => s.SignalId == signal.SignalId, ct);
        var requestRow = await db.Set<TenantApprovalRecord>().SingleOrDefaultAsync(r => r.TenantId == signal.TenantId && r.RequestId == signal.RequestId, ct);
        var state = await ReadTenantAsync(db, signal.TenantId, ct);
        if (saved != signal || saved?.ProcessedAt is not null || requestRow is null || state?.Tenant.Revision != expectedSecurityRevision) return false;
        var request = requestRow.Domain(); var p = request.Operation.AutomationAdmission ?? throw new ArgumentException("Exact Automation consent source required.");
        if (request.Status == TenantApprovalStatus.Pending || request.Revision != signal.DecisionRevision || request.RunId != signal.RunId || at < signal.CreatedAt) return false;
        var row = await db.TriggerOccurrences.IgnoreQueryFilters().SingleOrDefaultAsync(o => o.TenantId == request.TenantId && o.OccurrenceId == p.Occurrence.OccurrenceId.ToString("D"), ct);
        if (row is null || !TenantAutomationAdmissionApprovalRules.SameOccurrence(TriggerStoreMapping.ToOccurrence(row), TenantAutomationAdmissionApprovalRules.Pending(p))) return false;
        var source = TriggerStoreMapping.ToOccurrence(row); var grant = await db.Set<TenantOperationGrant>().SingleOrDefaultAsync(g => g.TenantId == request.TenantId && g.RequestId == request.RequestId, ct);
        var complete = false; var reason = request.Status == TenantApprovalStatus.Approved ? refusal : TenantApprovalRules.DuplicateReason(request);
        if (request.Status == TenantApprovalStatus.Approved)
        {
            try
            {
                var agent = await RequireAutomationAdmissionOperation(db, request, state, ct);
                var decision = await db.Set<TenantApprovalDecision>().SingleOrDefaultAsync(d => d.TenantId == request.TenantId && d.RequestId == request.RequestId, ct)
                    ?? throw new AccessDeniedException("approval_unavailable");
                TenantAutomationAdmissionApprovalRules.Decision(request, decision, grant, at);
                await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct);
                if (admission is not null)
                {
                    if (admission.Run.CreatedAtUtc < signal.CreatedAt || admission.Run.CreatedAtUtc > at) throw new SecurityConflictException("operation_witness_changed");
                    TenantAutomationRules.Admission(admission, agent, request.OriginalInitiatorPrincipalId, p); await RequireAutomationConversationAdmission(db, state, admission, request.OriginalInitiatorPrincipalId, p, false, ct); await RequireTenantEventAdmission(db, admission, p, true, ct); complete = true;
                }
                else if (string.IsNullOrWhiteSpace(reason)) throw new SecurityConflictException("operation_mismatch");
            }
            catch (AccessDeniedException error) { reason = error.ReasonCode; }
            catch (SecurityConflictException error) when (error.ReasonCode == "parent_busy") { return false; }
            catch (SecurityConflictException error) { reason = error.ReasonCode; }
        }
        if (complete) await StageTenantAutomationGraph(db, admission!, ct);
        db.Entry(row).CurrentValues.SetValues(TriggerStoreMapping.ToRecord(complete ? admission!.Occurrence
            : source.WithRouting(OccurrenceRoutingDisposition.Rejected, reason ?? "automation_admission_unavailable", source.RoutingRevision + 1, at, null, null)));
        if (p.EventSource is not null) await db.SaveChangesAsync(ct); // Source guards observe the settled occurrence inside this transaction.
        await SettleTenantEventSource(db, p.EventSource, p.Occurrence.OccurrenceId, false, complete ? null : reason ?? "automation_admission_unavailable", ct);
        if (grant is { Status: OperationGrantStatus.Available }) db.Entry(grant).CurrentValues.SetValues(grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = grant.Revision + 1 });
        db.Entry(saved!).CurrentValues.SetValues(signal with { Revision = signal.Revision + 1, ProcessedAt = at });
        var tenant = await db.Set<Tenant>().SingleAsync(t => t.TenantId == request.TenantId, ct); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = tenant.Revision + 1 });
        db.Add(new AuthorizationAudit(auditId, request.TenantId, request.ExecutorPrincipalId, "automations.admission.settle", p.Current.AutomationId.ToString("D"), complete ? "allowed" : "denied", complete ? "automation_occurrence_admitted" : reason ?? "automation_admission_unavailable", at));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException error) when (IsConstraint(error)) { throw new SecurityConflictException("automation_admission_conflict"); }
    }
}
