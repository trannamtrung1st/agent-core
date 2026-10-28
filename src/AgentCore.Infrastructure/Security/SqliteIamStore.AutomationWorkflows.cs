using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    private async Task RequireAutomationWorkflowOrigin(AgentCoreDbContext db, AgentCore.Domain.Security.TenantSecurityState state, WorkflowExecution execution, CancellationToken ct)
    {
        var occurrenceRow = await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(o => o.TenantId == execution.TenantId && o.OccurrenceId == execution.Source.SourceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_source_unavailable"); var occurrence = TriggerStoreMapping.ToOccurrence(occurrenceRow);
        var automationRow = await OwnedAutomations(db, execution.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.AutomationId == occurrence.AutomationId!.Value.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_source_unavailable"); var automation = TriggerStoreMapping.ToRegistration(automationRow);
        var agentRow = await OwnedAgents(db, execution.TenantId).SingleOrDefaultAsync(a => a.Instance.InstanceId == occurrence.Owner.AgentInstanceId.ToString("D"), ct)
            ?? throw new AccessDeniedException("workflow_source_unavailable"); var agent = Owned(agentRow);
        await WorkflowAutomationRules.AcceptedAsync(state, execution, occurrence, automation, agent,
            InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides), new(this), ct);
        if (WorkflowAutomationRules.EventSource(occurrence) is { } source)
            foreach (var id in source.EventIds)
            {
                var delivery = await TenantEvent(db, execution.TenantId, source.SourceKind, id, source.TriggerId, ct) ?? throw new AccessDeniedException("event_source_unavailable");
                if (delivery.Subscription.Owner != occurrence.Owner || delivery.Subscription.AutomationId != occurrence.AutomationId) throw new AccessDeniedException("event_source_unavailable");
                await TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, delivery.Subscription, [id], new(this), ct, TenantEventAdmissionRules.EmailAccounts(delivery));
                if (delivery.Core?.Event.Key == "email.message.received" && !await CanDeliverTenantEmail(db, execution.TenantId, id, ct)) throw new AccessDeniedException("email_event_source_changed");
                if (source.SourceKind == TriggerSourceKind.ApplicationEvent && !await db.WebhookEvents.IgnoreQueryFilters().AnyAsync(w => w.TenantId == execution.TenantId && w.ResourceId == source.ResourceId!.Value.ToString("D") && w.Status == (int)AgentCore.Domain.Events.WebhookEventStatus.Active, ct)) throw new AccessDeniedException("event_source_unavailable");
            }
    }
    public async ValueTask<TenantAutomationAdmissionResult?> AdmitAutomationWorkflowAsync(AutomationWorkflowAdmission command, long expectedSecurityRevision,
        HumanSessionReference? human, Guid auditId, CancellationToken ct = default)
    {
        var value = command.Execution; var tenant = value.TenantId;
        await using var db = await contexts.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var state = await ReadTenantAsync(db, tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
        if (human is not null) await RequireHumanSessionAsync(db, tenant, human, ct);
        var previous = await db.TriggerOccurrences.IgnoreQueryFilters().SingleOrDefaultAsync(o => o.TenantId == tenant && o.OccurrenceId == command.InitialOccurrence.OccurrenceId.ToString("D"), ct);
        if (previous is not null) return TenantAutomationRules.Existing(TriggerStoreMapping.ToOccurrence(previous));
        var savedRow = await OwnedAutomations(db, tenant).SingleOrDefaultAsync(a => a.AutomationId == command.Current.AutomationId.ToString("D"), ct);
        if (state.Tenant.Revision != expectedSecurityRevision || savedRow is null || !TenantAutomationRules.Same(TriggerStoreMapping.ToRegistration(savedRow), command.Current)) return null;
        var saved = command.Current;
        var agentRow = await OwnedAgents(db, tenant).SingleOrDefaultAsync(a => a.Instance.InstanceId == saved.Owner.AgentInstanceId.ToString("D"), ct) ?? throw new AccessDeniedException("agent_unavailable");
        var agent = Owned(agentRow); var policy = InstanceSettingsResolver.Resolve(await TenantAgentDefinitionBaseline(db, agent, ct), agent.Instance.SettingsOverrides);
        var version = await WorkflowVersionAt(db, value, ct);
        WorkflowAutomationRules.Admission(command, agent, policy, version, human?.PrincipalId);
        await WorkflowAutomationRules.AuthorityAsync(state, saved, agent, policy, human?.PrincipalId, command.EventSource, new(this), ct, emailAccounts: WorkflowAutomationRules.EmailAccounts(saved, command.InitialOccurrence));
        await WorkflowAutomationRules.DestinationAsync(state, saved, version, agent.PrincipalId, new(this), ct);
        if (human is not null) await RequireAutomationHuman(state, human.PrincipalId, agent, saved.AutomationId, "automations.run", false, ct);
        var definition = await db.Set<WorkflowDefinitionRow>().SingleOrDefaultAsync(r => r.TenantId == tenant && r.WorkflowId == value.WorkflowId, ct);
        if (definition is null || WorkflowPersistence.Read<WorkflowDefinition>(definition.Json).Retired) throw new AccessDeniedException("workflow_version_unavailable");
        if (await TenantAutomationBusy(db, tenant, saved.AutomationId, ct)) return null;
        if (command.EventSource is { } source) await RequireTenantEventSource(db, saved, command.InitialOccurrence, source, false, ct);
        if (await db.Set<WorkflowExecutionRow>().CountAsync(e => e.TenantId == tenant && e.Status < (int)WorkflowExecutionStatus.Completed, ct) >= 32) throw new SecurityConflictException("workflow_capacity_exceeded");
        await RequireWorkflowParticipants(db, state, value, version, ct);
        // Three writes within the owning Core transaction avoid a fictitious Run and permit exact two-way source guards.
        var occurrenceRow = TriggerStoreMapping.ToRecord(command.InitialOccurrence); db.TriggerOccurrences.Add(occurrenceRow); await db.SaveChangesAsync(ct);
        db.Add(WorkflowPersistence.Row(value)); await db.SaveChangesAsync(ct);
        var accepted = command.InitialOccurrence.WithWorkflowAcceptance(value.ExecutionId, value.CreatedAt);
        TriggerStoreMapping.CopyRouting(occurrenceRow, accepted);
        // Delivery/bucket guards inspect the accepted occurrence, so flush that link before their update.
        await db.SaveChangesAsync(ct);
        StageAutomation(db, savedRow, command.Next);
        await SettleTenantEventSource(db, command.EventSource, accepted.OccurrenceId, false, null, ct); await BumpTaskSecurity(db, state, ct);
        db.Add(new AgentCore.Domain.Security.AuthorizationAudit(auditId, tenant, agent.PrincipalId, "workflows.start", value.ExecutionId.ToString("D"), "allowed", "automation_occurrence", TaskNow));
        await SaveTaskChanges(db, ct); await tx.CommitAsync(ct); return new(true, accepted, null, null, WorkflowExecutionId: value.ExecutionId);
    }
}
