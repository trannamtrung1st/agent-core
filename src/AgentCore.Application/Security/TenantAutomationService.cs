using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationChange(bool Enabled, string Name, string Instructions, TriggerSchedule Schedule,
    string? ModelKey = null, string? ReasoningEffort = null, bool RequiresTools = true, bool RequiresVision = false);

public sealed partial class TenantAutomationService(IIamStore security, ITenantAutomationStore store, ITenantAgentStore agents,
    TenantRunConfigurationResolver configurations, IdentityService identity, IamAuthorizer iam, IModelCatalog models, IIdGenerator ids, TimeProvider time)
{
    private readonly SemaphoreSlim dueGate = new(1, 1);
    private string? dueCursor;
    public async ValueTask<TenantAutomationPage> ListAsync(Guid tenant, Guid instance, HumanSessionReference human, int page = 0, int pageSize = 30, CancellationToken ct = default)
    {
        if (page is < 0 or > 100000 || pageSize is < 1 or > 100) throw new ArgumentException("Use a bounded Automation page.");
        var state = await Human(tenant, human, ct); await AgentRead(state, instance, human.PrincipalId, ct);
        IReadOnlyList<Guid>? allowed = null;
        if (!(await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("automations.read"), AccessScope.ForTenant(tenant), ct)).Allowed)
        {
            var candidates = state.Assignments.Where(a => a.PrincipalId == human.PrincipalId && a.Scope.ResourceType == "automation"
                && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("automations.read"))).Select(a => a.Scope).Distinct().ToArray();
            var visible = new List<Guid>();
            foreach (var scope in candidates)
                if (TenantAutomationRules.TryId(scope, out var id) && (await iam.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("automations.read"), scope, ct)).Allowed) visible.Add(id);
            allowed = visible;
        }
        var result = await store.ListTenantAutomationsAsync(tenant, instance, allowed, page, pageSize, ct);
        await Unchanged(state, human, ct); return result;
    }
    public async ValueTask<Automation> ReadAsync(Guid tenant, Guid automation, HumanSessionReference human, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); var value = await store.FindTenantAutomationAsync(tenant, automation, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await AgentRead(state, value.Owner.AgentInstanceId, human.PrincipalId, ct); await Require(state, human.PrincipalId, "automations.read", TenantAutomationRules.Scope(tenant, automation), ct);
        await Unchanged(state, human, ct); return value;
    }
    public async ValueTask<Automation> SaveAsync(Guid tenant, Guid instance, Guid? automation, HumanSessionReference human, TenantAutomationChange change,
        long securityRevision, long instanceRevision, long automationRevision, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); await AgentRead(state, instance, human.PrincipalId, ct);
        var permission = automation is null ? "automations.create" : "automations.update";
        await Require(state, human.PrincipalId, permission, automation is { } id ? TenantAutomationRules.Scope(tenant, id) : AccessScope.ForTenant(tenant), ct);
        var current = automation is { } existing ? await store.FindTenantAutomationAsync(tenant, existing, ct) ?? throw new AccessDeniedException("resource_unavailable") : null;
        var (agent, policy) = await configurations.CurrentPolicyAsync(tenant, instance, ct);
        if (agent.Instance.Revision != instanceRevision || state.Tenant.Revision != securityRevision || (current?.Revision ?? 0) != automationRevision) throw new SecurityConflictException("stale_revision");
        if (current is not null) TenantAutomationRules.Owner(current, agent);
        if (agent.Instance.Lifecycle != AgentCore.Domain.Definitions.AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        if (current?.Status is AutomationStatus.Cancelled or AutomationStatus.Completed or AutomationStatus.Expired) throw new ArgumentException("A terminal Automation cannot be re-authored.");
        ExecutionModelPolicy.RequireSelectable(models, change.ModelKey, change.ReasoningEffort);
        var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var child = current?.Triggers.Single(); var trigger = new ScheduleTrigger(change.Schedule);
        var changed = child is not null && !child.Configuration.SemanticEquals(trigger);
        var proposed = new Automation(current?.AutomationId ?? ids.NewId(), TriggerOwner.ForTenant(tenant, instance, agent.PrincipalId),
            change.Enabled ? AutomationStatus.Active : AutomationStatus.Disabled, change.Instructions,
            new[] { new AutomationTriggerRecord(child?.TriggerId ?? ids.NewId(), trigger, true, child is null ? 1 : child.Revision + (changed ? 1 : 0)) },
            change.Enabled ? TriggerScheduleCalculator.InitialNext(change.Schedule, at) : current?.NextOccurrenceAtUtc, current?.ExpiresAtUtc, current?.OccurrenceCount ?? 0,
            automationRevision + 1, current is null ? 1 : current.TriggerRevision + (changed ? 1 : 0),
            current?.Provenance.WithUpdated(at) ?? new(TriggerAuthorizationOrigin.AdminOwner, null, null, at, at), null, change.ModelKey, change.ReasoningEffort,
            change.RequiresVision, change.Name, AutomationExecutionTarget.Background, AutomationCompletionDelivery.None, change.RequiresTools);
        TenantAutomationRules.Configuration(proposed, policy, at);
        if (proposed.Status == AutomationStatus.Active && (proposed.NextOccurrenceAtUtc is null || !ExecutionModelPolicy.Resolve(models, policy, agent.Instance, proposed).Accepted))
            throw new AccessDeniedException("automation_model_unavailable");
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, permission, proposed.AutomationId.ToString("D"), "allowed", "automation_saved", at);
        if (!await store.TrySaveTenantAutomationAsync(Next(state), securityRevision, proposed, automationRevision, instanceRevision, human, permission, audit, ct)) throw new SecurityConflictException("stale_revision");
        return proposed;
    }
    public async ValueTask<Automation> CancelAsync(Guid tenant, Guid automation, HumanSessionReference human, long securityRevision, long instanceRevision, long automationRevision, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); var current = await ReadAsync(tenant, automation, human, ct);
        await Require(state, human.PrincipalId, "automations.delete", TenantAutomationRules.Scope(tenant, automation), ct);
        if (state.Tenant.Revision != securityRevision || current.Revision != automationRevision) throw new SecurityConflictException("stale_revision");
        var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow()); var cancelled = current.WithCancellation(current.Revision + 1, at);
        if (!await store.TrySaveTenantAutomationAsync(Next(state), securityRevision, cancelled, automationRevision, instanceRevision, human, "automations.delete",
            new(ids.NewId(), tenant, human.PrincipalId, "automations.delete", automation.ToString("D"), "allowed", "automation_cancelled", at), ct)) throw new SecurityConflictException("stale_revision");
        return cancelled;
    }
    public async ValueTask<TenantAutomationAdmissionResult> RunAsync(Guid tenant, Guid automation, HumanSessionReference human,
        long securityRevision, long instanceRevision, long automationRevision, CancellationToken ct = default)
    {
        var state = await Human(tenant, human, ct); var value = await ReadAsync(tenant, automation, human, ct);
        await Require(state, human.PrincipalId, "automations.run", TenantAutomationRules.Scope(tenant, automation), ct);
        if (state.Tenant.Revision != securityRevision || value.Revision != automationRevision) throw new SecurityConflictException("stale_revision");
        var admitted = await Admit(value, state, human, instanceRevision, manual: true, ct);
        return admitted ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<int> AdmitDueAsync(CancellationToken ct = default)
    {
        await dueGate.WaitAsync(ct);
        try
        {
            var due = await store.ListDueTenantAutomationsAsync(time.GetUtcNow(), 100, ct, dueCursor); var count = 0;
            foreach (var value in due)
            {
                ct.ThrowIfCancellationRequested(); dueCursor = value.AutomationId.ToString("D");
                try
                {
                    var state = await security.ReadTenantAsync(value.Owner.TenantId!.Value, ct) ?? throw new AccessDeniedException("tenant_unavailable");
                    var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
                    var decision = TriggerScheduleAdmission.Decide(value, at);
                    if (decision.Kind is ScheduleAdmissionKind.Expire or ScheduleAdmissionKind.Complete)
                        await store.TryAdvanceTenantAutomationAsync(Next(state), state.Tenant.Revision, value, TriggerScheduleAdmission.Advance(value, decision, at),
                            new(ids.NewId(), state.Tenant.TenantId, value.Owner.ExecutorPrincipalId!.Value, "automations.schedule.advance", value.AutomationId.ToString("D"), "allowed", "schedule_closed", at), ct);
                    else if (await Admit(value, state, null, null, false, ct) is { Created: true }) count++;
                }
                catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or ArgumentException) { }
            }
            if (due.Count < 100) dueCursor = null;
            return count;
        }
        finally { dueGate.Release(); }
    }
    private async ValueTask<TenantAutomationAdmissionResult?> Admit(Automation current, TenantSecurityState state, HumanSessionReference? human, long? instanceRevision, bool manual, CancellationToken ct)
    {
        var tenant = current.Owner.TenantId!.Value; var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var (agent, resolved) = await configurations.ResolveAsync(tenant, current.Owner.AgentInstanceId, ct);
        if (instanceRevision is { } expected && agent.Instance.Revision != expected) throw new SecurityConflictException("stale_revision");
        await TenantAutomationRules.AdmissionAuthority(state, current, agent, resolved.Configuration.Definition, iam, ct, human?.PrincipalId);
        var model = ExecutionModelPolicy.Resolve(models, resolved.Configuration.Definition, agent.Instance, current);
        if (!model.Accepted || model.Pin is null) throw new AccessDeniedException("automation_model_unavailable");
        TriggerOccurrence occurrence; Automation next;
        if (manual)
        {
            occurrence = AutomationRules.ManualOccurrence(current, model.Pin, at);
            next = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, at, null);
        }
        else
        {
            var decision = TriggerScheduleAdmission.Decide(current, at);
            if (decision.Kind != ScheduleAdmissionKind.Admit) return null;
            occurrence = TriggerScheduleAdmission.CreateOccurrence(current, decision, at).WithModelPin(model.Pin);
            next = TriggerScheduleAdmission.Advance(current, decision, at);
        }
        var graph = BackgroundSessionAdmissionFactory.ForOccurrence(ids.NewSessionId(), ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId(), occurrence,
            resolved.Configuration.Definition, agent.Instance.Persona, agent.Instance.PersonaRevision, model.Pin, resolved.Skills, at, current.Name,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved.Configuration, manual ? human?.PrincipalId : null);
        occurrence = occurrence.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, null, occurrence.RoutingRevision + 1, at, null, null)
            .WithExecutionAcceptance(graph.Session.SessionId, graph.Run.AgentRunId, occurrence.RoutingRevision + 1, at);
        var admission = new TenantAutomationAdmission(current, next, occurrence, new(tenant, graph.Session.SessionId, agent.Instance.InstanceId, agent.PrincipalId, agent.PrincipalId, at),
            graph.Session, graph.Run, agent.Instance.Revision, TenantAutomationRules.Hash(resolved));
        TenantAutomationRules.Admission(admission, agent, human?.PrincipalId);
        return await store.TryAdmitTenantAutomationAsync(Next(state), state.Tenant.Revision, admission, human,
            new(ids.NewId(), tenant, agent.PrincipalId, "automations.occurrence.admit", current.AutomationId.ToString("D"), "allowed", manual ? "manual_occurrence_admitted" : "scheduled_occurrence_admitted", at), ct);
    }
    private ValueTask<TenantSecurityState> Human(Guid tenant, HumanSessionReference human, CancellationToken ct) => TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
    private async ValueTask Unchanged(TenantSecurityState before, HumanSessionReference human, CancellationToken ct)
    { if ((await Human(before.Tenant.TenantId, human, ct)).Tenant.Revision != before.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask AgentRead(TenantSecurityState state, Guid instance, Guid human, CancellationToken ct)
    {
        if (await agents.FindTenantAgentAsync(state.Tenant.TenantId, instance, ct) is null) throw new AccessDeniedException("resource_unavailable");
        await Require(state, human, "agents.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", instance.ToString("D")), ct);
    }
    private async ValueTask Require(TenantSecurityState state, Guid principal, string key, AccessScope scope, CancellationToken ct)
    { if (!(await iam.CheckTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct)).Allowed) throw new AccessDeniedException("permission_denied"); }
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
}
