using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public sealed record TenantAutomationTriggerChange(Guid? TriggerId, AutomationTrigger Configuration, bool Enabled = true, long ExpectedRevision = 0);
public sealed record TenantAutomationChange(bool Enabled, string Name, string Instructions, TriggerSchedule? Schedule,
    string? ModelKey = null, string? ReasoningEffort = null, bool RequiresTools = true, bool RequiresVision = false,
    IReadOnlyList<TenantAutomationTriggerChange>? Events = null, Guid? ReportToSessionId = null, Guid? TargetSessionId = null,
    string? PresetId = null, int? PresetVersion = null);

public sealed partial class TenantAutomationService(IIamStore security, ITenantAutomationStore store, ITenantAgentStore agents,
    TenantRunConfigurationResolver configurations, IdentityService identity, IamAuthorizer iam, IModelCatalog models, IIdGenerator ids, TimeProvider time, IEventFilterEvaluator? filters = null, ITenantWebhookStore? webhooks = null, ITenantEmailWatchStore? emailWatches = null, ITenantSessionStore? sessions = null)
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
        if (change.Enabled || current is null || current.ModelOverrideCatalogKey != change.ModelKey || current.ModelOverrideReasoningEffort != change.ReasoningEffort)
            ExecutionModelPolicy.RequireSelectable(models, change.ModelKey, change.ReasoningEffort);
        var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var children = Children(current, change);
        var isSchedule = children.Count == 1 && children[0].Configuration is ScheduleTrigger;
        var proposed = new Automation(current?.AutomationId ?? ids.NewId(), TriggerOwner.ForTenant(tenant, instance, agent.PrincipalId),
            change.Enabled ? AutomationStatus.Active : AutomationStatus.Disabled, change.Instructions,
            children,
            isSchedule ? change.Enabled ? TriggerScheduleCalculator.InitialNext(((ScheduleTrigger)children[0].Configuration).Schedule, at) : current?.IsSchedule == true ? current.NextOccurrenceAtUtc : null : null, current?.ExpiresAtUtc, current?.OccurrenceCount ?? 0,
            automationRevision + 1, children.Max(t => t.Revision),
            TenantAutomationPresetRules.Provenance(current, change, at), null, change.ModelKey, change.ReasoningEffort,
            change.RequiresVision, change.Name, change.TargetSessionId is { } targetId ? AutomationExecutionTarget.Existing(targetId) : AutomationExecutionTarget.Background, change.ReportToSessionId is { } destination ? AutomationCompletionDelivery.ToSession(destination) : AutomationCompletionDelivery.None, change.RequiresTools);
        TenantAutomationRules.Configuration(proposed, policy, at);
        await TenantAutomationRules.HumanSourcesAsync(state, human.PrincipalId, proposed, iam, ct);
        await TenantCompletionDestinationRules.AuthoringAsync(state, human.PrincipalId, proposed,
            proposed.CompletionDelivery.SessionId is { } reportTo && sessions is not null ? await sessions.FindTenantSessionAsync(tenant, reportTo, ct) : null, iam, ct);
        var conversation = TenantAutomationConversationRules.Existing(proposed)
            ? TenantAutomationConversationRules.Require(proposed, sessions is null ? null : await sessions.FindTenantSessionAsync(tenant, proposed.ExecutionTarget.SessionId!.Value, ct)) : null;
        await TenantAutomationConversationRules.AuthoringAsync(state, human.PrincipalId, proposed, conversation, iam, ct);
        if (conversation is not null) _ = TenantAutomationConversationRules.Model(proposed, conversation, policy, models);
        if (proposed.Status == AutomationStatus.Active && (proposed.IsSchedule && proposed.NextOccurrenceAtUtc is null || conversation is null && !ExecutionModelPolicy.Resolve(models, policy, agent.Instance, proposed).Accepted))
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
    private async ValueTask<TenantAutomationAdmissionResult?> Admit(Automation current, TenantSecurityState state, HumanSessionReference? human, long? instanceRevision, bool manual, CancellationToken ct, TenantEventDelivery? eventDelivery = null, TenantEventBucket? eventBucket = null)
    {
        var tenant = current.Owner.TenantId!.Value; var at = TriggerScheduleCalculator.Truncate(time.GetUtcNow());
        var (agent, resolved) = await configurations.ResolveAsync(tenant, current.Owner.AgentInstanceId, ct);
        if (instanceRevision is { } expected && agent.Instance.Revision != expected) throw new SecurityConflictException("stale_revision");
        PreparedEventAdmissionSource? eventSource = null;
        long? sourceRevision = null;
        if (eventDelivery is not null || eventBucket is not null)
        {
            if (manual || human is not null || eventDelivery is not null && eventBucket is not null) throw new ArgumentException("An independent event has exactly one owned source.");
            var subscription = eventDelivery?.Subscription ?? eventBucket!.Bucket.Subscription;
            if (subscription.Owner != current.Owner || subscription.AutomationId != current.AutomationId) throw new AccessDeniedException("event_source_unavailable");
            if (subscription.SourceKind == TriggerSourceKind.ApplicationEvent)
            {
                var source = webhooks is null || subscription.ResourceId is not { } id ? null : await webhooks.FindTenantWebhookAsync(tenant, id, ct);
                if (source?.Status != AgentCore.Domain.Events.WebhookEventStatus.Active) throw new AccessDeniedException("event_source_unavailable");
                sourceRevision = source.Revision;
            }
            if (eventBucket is not null && TenantEventAdmissionRules.EmailBucket(eventBucket.Bucket))
            {
                var eligible = new List<Guid>();
                foreach (var entry in eventBucket.Bucket.Sources)
                {
                    if (emailWatches is null || !await emailWatches.CanDeliverTenantEmailEventAsync(tenant, entry.EventId, ct)) continue;
                    try
                    {
                        await TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, subscription, [entry.EventId], iam, ct,
                            [AgentCore.Domain.Email.EmailEventReferences.AccountId(entry.DataJson)]);
                        eligible.Add(entry.EventId);
                    }
                    catch (AccessDeniedException) { }
                }
                if (eligible.Count == 0) throw new AccessDeniedException("email_event_source_unavailable");
                eventBucket = TenantEventAdmissionRules.Retain(eventBucket, eligible);
            }
            eventSource = eventDelivery is not null ? TenantEventAdmissionRules.Freeze(eventDelivery, sourceRevision) : TenantEventAdmissionRules.Freeze(eventBucket!, sourceRevision);
            await TenantEventAdmissionRules.RawReadAsync(state, agent.PrincipalId, subscription, eventSource.EventIds, iam, ct, eventDelivery is not null ? TenantEventAdmissionRules.EmailAccounts(eventDelivery) : TenantEventAdmissionRules.EmailAccounts(eventBucket!));
        }
        var sourceKind = manual ? TriggerSourceKind.ManualInvocation : eventSource?.SourceKind ?? TriggerSourceKind.Schedule;
        var conversation = TenantAutomationConversationRules.Existing(current)
            ? TenantAutomationConversationRules.Require(current, sessions is null ? null : await sessions.FindTenantSessionAsync(tenant, current.ExecutionTarget.SessionId!.Value, ct)) : null;
        var admissionDecision = await TenantAutomationAdmissionApprovalRules.EvaluateAsync(state, current, agent, resolved.Configuration.Definition, human?.PrincipalId, iam, ct, sourceKind, eventSource, eventDelivery is not null ? TenantEventAdmissionRules.EmailAccounts(eventDelivery) : eventBucket is not null ? TenantEventAdmissionRules.EmailAccounts(eventBucket) : []);
        var model = conversation is null ? ExecutionModelPolicy.Resolve(models, resolved.Configuration.Definition, agent.Instance, current)
            : new ExecutionModelDecision(TenantAutomationConversationRules.Model(current, conversation, resolved.Configuration.Definition, models), null);
        if (!model.Accepted || model.Pin is null) throw new AccessDeniedException("automation_model_unavailable");
        TriggerOccurrence occurrence; Automation next;
        if (manual)
        {
            occurrence = AutomationRules.ManualOccurrence(current, model.Pin, at);
            next = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, at, null);
        }
        else if (eventSource is not null)
        {
            occurrence = eventDelivery is not null ? TenantEventAdmissionRules.Occurrence(current, eventDelivery, model.Pin, at, sourceRevision)
                : TenantEventAdmissionRules.Occurrence(current, eventBucket!, model.Pin, at, sourceRevision);
            next = TenantEventAdmissionRules.Next(current, at);
        }
        else
        {
            var decision = TriggerScheduleAdmission.Decide(current, at);
            if (decision.Kind != ScheduleAdmissionKind.Admit) return null;
            occurrence = TriggerScheduleAdmission.CreateOccurrence(current, decision, at).WithModelPin(model.Pin);
            next = TriggerScheduleAdmission.Advance(current, decision, at);
        }
        var graph = conversation is null ? BackgroundSessionAdmissionFactory.ForOccurrence(ids.NewSessionId(), ids.NewId(), ids.NewId(), ids.NewId(), ids.NewId(), occurrence,
            resolved.Configuration.Definition, agent.Instance.Persona, agent.Instance.PersonaRevision, model.Pin, resolved.Skills, at, current.Name,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved.Configuration, manual ? human?.PrincipalId : null)
            : (Session: conversation.Snapshot, Run: TenantAutomationConversationRules.ForOccurrence(conversation, occurrence, agent, resolved,
                ids.NewId(), ids.NewId(), ids.NewId(), model.Pin, at, manual ? human?.PrincipalId : null));
        var initialOccurrence = occurrence;
        occurrence = occurrence.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, null, occurrence.RoutingRevision + 1, at, null, null)
            .WithExecutionAcceptance(graph.Session.SessionId, graph.Run.AgentRunId, occurrence.RoutingRevision + 1, at);
        var admission = new TenantAutomationAdmission(current, next, occurrence, conversation?.Owner ?? new(tenant, graph.Session.SessionId, agent.Instance.InstanceId, agent.PrincipalId, agent.PrincipalId, at),
            graph.Session, graph.Run, agent.Instance.Revision, TenantAutomationRules.Hash(resolved), eventSource, conversation?.Snapshot.Revision);
        TenantAutomationRules.Admission(admission, agent, human?.PrincipalId);
        if (admissionDecision.Kind is { } kind)
        {
            var requestId = ids.NewId();
            var initial = initialOccurrence;
            var intent = new PreparedAutomationAdmission(tenant, current, next, initial, requestId, graph.Session.SessionId, conversation is null ? graph.Session.Entries[0].EntryId : ids.NewId(),
                graph.Run.ActivationId, graph.Run.AgentRunId, graph.Run.ResponseId!.Value, human?.PrincipalId, admissionDecision.Permission,
                TenantAutomationRules.Hash(resolved), new(model.Pin.CatalogKey, model.Pin.ProviderAlias, model.Pin.ModelId, model.Pin.ReasoningEffort), eventSource,
                conversation is null ? null : new(conversation.Snapshot.Revision, conversation.Snapshot.DurableLastEntrySequence));
            var op = TenantApprovalRules.PrepareAutomationAdmission(ids.NewId(), agent.Instance.InstanceId, intent, agent.Instance.Revision, agent.Instance.PersonaRevision);
            var request = new TenantApprovalRequest(requestId, tenant, intent.RunId, intent.SessionId, intent.ActivationId, agent.PrincipalId, human?.PrincipalId,
                kind, op, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), new(agent.Instance.Persona.Name));
            var qualified = false;
            foreach (var user in state.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantAutomationAdmissionApprovalRules.ReviewerAsync(state, request, user.PrincipalId, iam, ct); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) throw new AccessDeniedException("no_eligible_approver");
            admission = TenantAutomationAdmissionApprovalRules.Build(request, agent, resolved, conversation: conversation);
            TenantAutomationRules.Admission(admission, agent, human?.PrincipalId, intent);
            return await store.TryPrepareTenantAutomationApprovalAsync(Next(state), state.Tenant.Revision, admission, request, human,
                new(ids.NewId(), tenant, agent.PrincipalId, "approvals.request", requestId.ToString("D"), "allowed", "automation_admission_pending", at), ct);
        }
        return await store.TryAdmitTenantAutomationAsync(Next(state), state.Tenant.Revision, admission, human,
            new(ids.NewId(), tenant, agent.PrincipalId, "automations.occurrence.admit", current.AutomationId.ToString("D"), "allowed", manual ? "manual_occurrence_admitted" : eventSource is null ? "scheduled_occurrence_admitted" : "event_occurrence_admitted", at), ct);
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
