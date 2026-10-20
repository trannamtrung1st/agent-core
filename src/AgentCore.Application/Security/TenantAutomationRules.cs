using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static class TenantAutomationRules
{
    public static AccessScope Scope(Guid tenant, Guid automation) => AccessScope.ForResource(tenant, "automation", automation.ToString("D"));
    public static bool TryId(AccessScope scope, out Guid id)
    {
        id = Guid.Empty;
        return scope.Kind == AccessScopeKind.Resource && scope.TenantId is { } tenant && tenant != Guid.Empty && scope.ResourceType == "automation"
            && Guid.TryParseExact(scope.ResourceId, "D", out id) && id != Guid.Empty && scope.ResourceId == id.ToString("D");
    }
    public static async ValueTask HumanAsync(TenantSecurityState state, Guid human, TenantAgent agent, Guid id, string permission, bool create, IamAuthorizer iam, CancellationToken ct)
    {
        if (permission is not ("automations.create" or "automations.update" or "automations.delete" or "automations.run") || create != (permission == "automations.create"))
            throw new ArgumentException("Invalid Automation operation.");
        if (!create && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get("automations.read"), Scope(state.Tenant.TenantId, id), ct)).Allowed) throw new AccessDeniedException("permission_denied");
        foreach (var (key, scope) in new[] { ("agents.read", AccessScope.ForResource(state.Tenant.TenantId, "agent", agent.Instance.InstanceId.ToString("D"))),
            (permission, create ? AccessScope.ForTenant(state.Tenant.TenantId) : Scope(state.Tenant.TenantId, id)) })
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct)).Allowed) throw new AccessDeniedException("permission_denied");
    }
    public static async ValueTask HumanSourcesAsync(TenantSecurityState state, Guid human, Automation value, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var child in value.Triggers)
        {
            var target = child.Configuration switch {
                EventTrigger webhook => ("webhooks.read", TenantWebhookRules.Scope(state.Tenant.TenantId, webhook.EventId)),
                CoreEventTrigger => ("core-events.read", AccessScope.ForTenant(state.Tenant.TenantId)),
                _ => ((string, AccessScope)?)null };
            if (target is { } source && !(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(source.Item1), source.Item2, ct)).Allowed)
                throw new AccessDeniedException("event_source_read_required");
        }
    }
    public static bool Same(Automation a, Automation b) => a.AutomationId == b.AutomationId && a.Owner == b.Owner && a.Status == b.Status
        && a.Revision == b.Revision && a.TriggerRevision == b.TriggerRevision && a.Name == b.Name && a.Instructions == b.Instructions
        && a.NextOccurrenceAtUtc == b.NextOccurrenceAtUtc && a.ExpiresAtUtc == b.ExpiresAtUtc && a.OccurrenceCount == b.OccurrenceCount
        && a.ModelOverrideCatalogKey == b.ModelOverrideCatalogKey && a.ModelOverrideReasoningEffort == b.ModelOverrideReasoningEffort
        && a.RequiresTools == b.RequiresTools && a.RequiresVision == b.RequiresVision && a.ExecutionTarget == b.ExecutionTarget && a.CompletionDelivery == b.CompletionDelivery
        && a.SuspensionReason == b.SuspensionReason && JsonSerializer.Serialize(a.Provenance) == JsonSerializer.Serialize(b.Provenance)
        && a.Triggers.Count == b.Triggers.Count && a.Triggers.Zip(b.Triggers).All(pair => pair.First.TriggerId == pair.Second.TriggerId
            && pair.First.Revision == pair.Second.Revision && pair.First.Enabled == pair.Second.Enabled && pair.First.Configuration.SemanticEquals(pair.Second.Configuration));
    public static TenantAutomationAdmissionResult Existing(TriggerOccurrence occurrence)
    {
        Guid? request = null;
        if (occurrence.Disposition == OccurrenceRoutingDisposition.AwaitingDurableWork && occurrence.DispositionReason == TenantAutomationAdmissionApprovalRules.PendingReason)
        {
            using var json = JsonDocument.Parse(occurrence.EvidenceJson);
            if (!json.RootElement.TryGetProperty("admissionApproval", out var consent) || !consent.TryGetProperty("requestId", out var id) || !id.TryGetGuid(out var parsed))
                throw new SecurityConflictException("operation_witness_changed");
            request = parsed;
        }
        return new(false, occurrence, occurrence.ExecutionSessionId, occurrence.AcceptedAgentRunId, request);
    }
    public static Guid SourceAutomation(AgentRun run)
    {
        var source = run.Admission.TaskIntent;
        var text = source?.SourceRevision;
        if (source?.Source != AgentRunTaskSource.Automation || text is null || text.Length < 38 || text[36] != ':'
            || !Guid.TryParseExact(text[..36], "D", out var id) || id == Guid.Empty) throw new AccessDeniedException("tenant_lineage_unavailable");
        return id;
    }
    public static string Hash(ResolvedAgentRunConfiguration configuration) => TenantWorkspaceMutationContract.Hash(JsonSerializer.Serialize(configuration));
    public static void Owner(Automation value, TenantAgent agent)
    {
        if (value.Owner != TriggerOwner.ForTenant(agent.TenantId, agent.Instance.InstanceId, agent.PrincipalId)) throw new AccessDeniedException("automation_unavailable");
    }
    public static void Configuration(Automation value, AgentDefinition definition, DateTimeOffset at, bool validateSchedulePolicy = true, TriggerSourceKind? sourceKind = null)
    {
        if (value.ExecutionTarget != AutomationExecutionTarget.Background
            || value.CompletionDelivery != AutomationCompletionDelivery.None || value.Provenance.SourceSessionId is not null || value.Provenance.SourceEventId is not null)
            throw new ArgumentException("Use the exact owned background destination.");
        if (!value.IsSchedule)
        {
            if (value.Triggers.Any(t => t.Configuration is not FilteredEventTrigger)) throw new ArgumentException("Use distinct typed event subscriptions.");
            if (value.Status == AutomationStatus.Active && validateSchedulePolicy && value.Triggers.Where(t => t.Enabled)
                .Any(t => (sourceKind is null || AutomationRules.Source(t.Configuration) == sourceKind) && !OccurrenceCompatibility.Allows(definition, AutomationRules.Source(t.Configuration))))
                throw new AccessDeniedException("event_policy_unavailable");
            return;
        }
        if (value.Status == AutomationStatus.Active && validateSchedulePolicy)
        {
            var policy = definition.TriggerPolicy ?? throw new AccessDeniedException("scheduling_unavailable");
            if (!TriggerDurableSchedulingPolicy.AllowsScheduledOccurrence(definition)) throw new AccessDeniedException("scheduling_unavailable");
            try { ScheduleDefinitionPolicy.Validate(((ScheduleTrigger)value.Trigger!).Schedule, policy, at, value.Instructions); }
            catch (TriggerScheduleCommandException e) { throw new ArgumentException(e.Message); }
        }
    }
    public static async ValueTask AdmissionAuthority(TenantSecurityState state, Automation automation, TenantAgent agent, AgentDefinition definition,
        IamAuthorizer iam, CancellationToken ct, Guid? requester = null, TriggerSourceKind? sourceKind = null, PreparedEventAdmissionSource? eventSource = null)
    {
        AdmissionSource(state, automation, agent, definition, requester, sourceKind);
        foreach (var (key, scope) in TenantAutomationAdmissionApprovalRules.Targets(state.Tenant.TenantId, automation.AutomationId).Concat(TenantAutomationAdmissionApprovalRules.SourceTargets(eventSource)))
        {
            if (!(await iam.CheckOwnedTenantAsync(state, agent.PrincipalId, PermissionCatalog.Get(key), scope, ct)).Allowed)
                throw new AccessDeniedException("automation_executor_required");
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(key)).Mode == OperationApprovalMode.Always
                || requester is not null && await TenantSandboxReadOperationRules.NeedsReviewAsync(state, requester, key, scope, iam, ct))
                throw new AccessDeniedException("automation_admission_review_required");
        }
    }
    public static void AdmissionSource(TenantSecurityState state, Automation automation, TenantAgent agent, AgentDefinition definition, Guid? requester, TriggerSourceKind? sourceKind = null)
    {
        Owner(automation, agent); TenantSessionRules.RequireExecutor(state, agent);
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || automation.Status != AutomationStatus.Active) throw new AccessDeniedException("automation_unavailable");
        Configuration(automation, definition, automation.Provenance.CreatedAt, requester is null, sourceKind);
    }
    public static void TechnicalAdvance(Automation current, Automation advanced, DateTimeOffset at)
    {
        var decision = TriggerScheduleAdmission.Decide(current, at);
        if (decision.Kind is not (ScheduleAdmissionKind.Expire or ScheduleAdmissionKind.Complete)
            || !Same(TriggerScheduleAdmission.Advance(current, decision, at), advanced))
            throw new ArgumentException("Only an exact non-executing schedule closure may advance without an occurrence.");
    }
    public static void Admission(TenantAutomationAdmission value, TenantAgent agent, Guid? manualInitiator = null, PreparedAutomationAdmission? consent = null)
    {
        var current = value.Current; var next = value.Next; var occurrence = value.Occurrence; var root = value.SessionOwner; var run = value.Run;
        Owner(current, agent); Owner(next, agent);
        if ((occurrence.SourceKind == TriggerSourceKind.ManualInvocation) != (manualInitiator is not null)) throw new ArgumentException("Manual admission requires its actual human invoker.");
        var context = run.Admission.ExecutionContext;
        var at = run.CreatedAtUtc;
        var sourceAt = consent?.Occurrence.AdmittedAtUtc ?? at;
        var pin = occurrence.ModelPin ?? throw new ArgumentException("An occurrence requires its exact model pin.");
        Automation expectedNext; TriggerOccurrence expectedOccurrence;
        if (occurrence.SourceKind == TriggerSourceKind.ManualInvocation)
        {
            expectedNext = current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc, current.OccurrenceCount, current.Revision + 1, sourceAt, null);
            expectedOccurrence = AutomationRules.ManualOccurrence(current, pin, sourceAt);
        }
        else if (occurrence.SourceKind is TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)
        {
            var source = value.EventSource ?? throw new ArgumentException("Event admission requires its owned source witness.");
            if (consent is not null && JsonSerializer.Serialize(source) != JsonSerializer.Serialize(consent.EventSource)) throw new ArgumentException("Event consent source differs.");
            expectedNext = TenantEventAdmissionRules.Next(current, sourceAt);
            expectedOccurrence = consent?.Occurrence ?? TenantEventAdmissionRules.Initial(occurrence);
            TenantEventAdmissionRules.Shape(current, expectedOccurrence, source);
        }
        else
        {
            if (value.EventSource is not null) throw new ArgumentException("A schedule cannot claim an event source.");
            var decision = TriggerScheduleAdmission.Decide(current, sourceAt);
            if (decision.Kind != ScheduleAdmissionKind.Admit) throw new ArgumentException("The exact schedule is not due.");
            expectedNext = TriggerScheduleAdmission.Advance(current, decision, sourceAt);
            expectedOccurrence = TriggerScheduleAdmission.CreateOccurrence(current, decision, sourceAt).WithModelPin(pin);
        }
        if (consent is not null)
        {
            if (!Same(consent.Current, current) || !Same(consent.Next, next) || !TenantAutomationAdmissionApprovalRules.SameOccurrence(consent.Occurrence, expectedOccurrence)) throw new ArgumentException("Consent source differs from the exact occurrence.");
            expectedOccurrence = TenantAutomationAdmissionApprovalRules.Source(consent);
        }
        expectedOccurrence = expectedOccurrence.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, null, expectedOccurrence.RoutingRevision + 1, sourceAt, null, null)
            .WithExecutionAcceptance(root.SessionId, run.AgentRunId, expectedOccurrence.RoutingRevision + 1, at);
        if (!Same(expectedNext, next) || JsonSerializer.Serialize(expectedOccurrence) != JsonSerializer.Serialize(occurrence))
            throw new ArgumentException("Occurrence intent differs from its current owned registration.");
        if (current.AutomationId != next.AutomationId || next.Revision != current.Revision + 1 || current.Owner != occurrence.Owner
            || occurrence.AutomationId != current.AutomationId || occurrence.SourceKind is not (TriggerSourceKind.Schedule or TriggerSourceKind.ManualInvocation or TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)
            || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable || occurrence.ExecutionSessionId != root.SessionId || occurrence.AcceptedAgentRunId != run.AgentRunId
            || root.TenantId != agent.TenantId || root.AgentInstanceId != agent.Instance.InstanceId || root.ExecutorPrincipalId != agent.PrincipalId
            || root.CreatedByPrincipalId != agent.PrincipalId || root.SessionId != value.Session.SessionId || root.CreatedAt != run.CreatedAtUtc
            || run.Owner != AgentRunOwner.ForTenant(agent.TenantId, agent.Instance.InstanceId, agent.PrincipalId) || run.SessionId != root.SessionId
            || !run.IsInitialQueued || context?.OriginalInitiatorPrincipalId != (occurrence.SourceKind == TriggerSourceKind.ManualInvocation ? manualInitiator : null) || context is null || context.ParentRunId is not null || context.ParentActivationId is not null
            || context.RootActivationId != run.ActivationId || context.Origin != (occurrence.SourceKind switch { TriggerSourceKind.Schedule => ExecutionOrigin.ScheduledOccurrence,
                TriggerSourceKind.ManualInvocation => ExecutionOrigin.ManualInvocation, TriggerSourceKind.CoreEvent => ExecutionOrigin.CoreEvent, _ => ExecutionOrigin.ApplicationEvent })
            || run.Admission.Activation.TriggerOccurrenceId != occurrence.OccurrenceId || run.Admission.Activation.DedupeKey != occurrence.DedupeKey
            || run.Admission.OutputContract != AgentRunOutputContract.BackgroundOutcome || value.Session.ProfileId is not null
            || value.Session.Origin.AutomationId != current.AutomationId || value.Session.Origin.TriggerOccurrenceId != occurrence.OccurrenceId
            || value.Session.Origin.InitialBackgroundAgentRunId != run.AgentRunId || value.Session.Surfaces != SessionSurface.BackgroundWork
            || run.Admission.Configuration?.InstanceRevision != agent.Instance.Revision || value.ExpectedInstanceRevision != agent.Instance.Revision
            || run.Admission.Configuration.PersonaRevision != agent.Instance.PersonaRevision || run.DefinitionId != agent.Instance.DefinitionId
            || run.DefinitionVersion != agent.Instance.ActiveVersion || run.PinnedPersona != agent.Instance.Persona)
            throw new ArgumentException("Invalid exact autonomous occurrence graph.");
        if (Hash(new(run.Admission.Configuration, run.PinnedPersona, run.PinnedSkillCatalog, agent.Instance.ExecutionBudgets)) != value.ConfigurationHash)
            throw new ArgumentException("Autonomous configuration witness changed.");
        var rebuilt = BackgroundSessionAdmissionFactory.ForOccurrence(run.SessionId, value.Session.Entries[0].EntryId, run.ActivationId, run.AgentRunId, run.ResponseId ?? throw new ArgumentException("Occurrence response identity is required."),
            occurrence, run.Admission.Configuration.Definition, agent.Instance.Persona, agent.Instance.PersonaRevision, pin, run.PinnedSkillCatalog, at, current.Name,
            ExecutionBudgetResolver.Resolve(run.Admission.Configuration.Definition, agent.Instance.ExecutionBudgets, false), run.Admission.Configuration, manualInitiator);
        if (JsonSerializer.Serialize(rebuilt.Session) != JsonSerializer.Serialize(value.Session) || JsonSerializer.Serialize(rebuilt.Run.Admission) != JsonSerializer.Serialize(run.Admission)
            || JsonSerializer.Serialize(rebuilt.Run.PinnedModel) != JsonSerializer.Serialize(run.PinnedModel) || rebuilt.Run.MaxAttempts != run.MaxAttempts
            || JsonSerializer.Serialize(rebuilt.Run.PinnedSkillCatalog) != JsonSerializer.Serialize(run.PinnedSkillCatalog)
            || rebuilt.Run.AgentRunId != run.AgentRunId)
            throw new ArgumentException("Autonomous Session/Run graph differs from its frozen occurrence.");
    }
}
