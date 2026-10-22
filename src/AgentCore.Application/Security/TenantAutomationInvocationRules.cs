using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// One manual occurrence through the incumbent Automation and Session/Run owners.
public static class TenantAutomationInvocationRules
{
    public static Guid Requester(TenantAutomationMutationObservation source) =>
        source.Reader.Root.Admission.ExecutionContext is { Origin: ExecutionOrigin.UserMessage, OriginalInitiatorPrincipalId: { } human }
            && source.Reader.User?.PrincipalId == human && !source.AdmittedTask ? human : throw new AccessDeniedException("automation_manual_request_required");

    public static void Validate(TenantAutomationMutationObservation source, PreparedAutomationMutation value)
    {
        _ = Requester(source); var args = TenantAutomationMutationContract.Parse(value.ToolName, value.ArgumentsJson);
        var current = source.Current ?? throw new AccessDeniedException("automation_unavailable"); var plan = value.Invocation ?? throw new SecurityConflictException("automation_plan_changed");
        var ids = new[] { plan.SessionId, plan.EntryId, plan.ActivationId, plan.RunId, plan.ResponseId };
        if (value.ToolName != ToolCatalog.AutomationRun || value.SessionId != source.Reader.Run.SessionId || value.RootSessionId != source.Reader.Root.SessionId
            || args.Json != value.ArgumentsJson || args.AutomationId != current.AutomationId || args.ExpectedRevision != current.Revision
            || value.Current is null || !TenantAutomationRules.Same(current, value.Current) || value.IntentHash != TenantAutomationMutationRules.IntentHash(source, args)
            || value.Proposed.Provenance.UpdatedAt < current.Provenance.UpdatedAt
            || current.Status != AutomationStatus.Active || !TenantAutomationRules.Same(current.WithScheduleAdvance(current.Status, current.NextOccurrenceAtUtc,
                current.OccurrenceCount, current.Revision + 1, value.Proposed.Provenance.UpdatedAt, null), value.Proposed)
            || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length || ids.Contains(source.Reader.Run.AgentRunId)
            || !TenantMemoryOperationRules.ValidHash(plan.ConfigurationHash) || plan.Model is null
            || (plan.Conversation is not null) != TenantAutomationConversationRules.Existing(current)
            || (plan.Conversation is not null ? plan.SessionId != current.ExecutionTarget.SessionId : plan.SessionId == value.SessionId))
            throw new SecurityConflictException("automation_plan_changed");
        if (plan.SessionId == value.SessionId) throw new AccessDeniedException("automation_target_busy");
        TenantAutomationRules.Owner(current, source.Reader.Agent);
        TenantAutomationRules.AdmissionSource(source.Reader.Security, current, source.Reader.Agent, source.Reader.Current, Requester(source), TriggerSourceKind.ManualInvocation);
        if (plan.Conversation is not null) _ = TenantAutomationConversationRules.Require(current, source.Conversation, plan.Conversation);
    }

    public static async ValueTask RequesterAsync(TenantAutomationMutationObservation source, IamAuthorizer iam, CancellationToken ct)
    {
        var current = source.Current!;
        await TenantAutomationRules.HumanAsync(source.Reader.Security, Requester(source), source.Reader.Agent, current.AutomationId, "automations.run", false, iam, ct).ConfigureAwait(false);
    }

    public static TenantAutomationAdmission Build(TenantAutomationMutationObservation source, PreparedAutomationMutation value,
        ResolvedAgentRunConfiguration resolved, DateTimeOffset at)
    {
        Validate(source, value); var plan = value.Invocation!; var current = value.Current!;
        if (TenantAutomationRules.Hash(resolved) != plan.ConfigurationHash || resolved.Configuration.InstanceRevision != source.Reader.Agent.Instance.Revision
            || resolved.Configuration.PersonaRevision != source.Reader.Agent.Instance.PersonaRevision) throw new SecurityConflictException("automation_configuration_changed");
        var preparedAt = value.Proposed.Provenance.UpdatedAt;
        var initial = AutomationRules.ManualOccurrence(current, plan.Model, preparedAt);
        var routed = initial.WithRouting(OccurrenceRoutingDisposition.AwaitingDurableWork, null, 1, preparedAt, null, null);
        var conversation = plan.Conversation is null ? null : TenantAutomationConversationRules.Require(current, source.Conversation, plan.Conversation);
        var graph = conversation is null ? BackgroundSessionAdmissionFactory.ForOccurrence(plan.SessionId, plan.EntryId, plan.ActivationId, plan.RunId, plan.ResponseId,
            initial, resolved.Configuration.Definition, resolved.Persona, resolved.Configuration.PersonaRevision, plan.Model, resolved.Skills, at, current.Name,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, false), resolved.Configuration, Requester(source))
            : (Session: conversation.Snapshot, Run: TenantAutomationConversationRules.ForOccurrence(conversation, initial, source.Reader.Agent, resolved,
                plan.ActivationId, plan.RunId, plan.ResponseId, plan.Model, at, Requester(source)));
        var accepted = routed.WithExecutionAcceptance(plan.SessionId, plan.RunId, routed.RoutingRevision, at);
        var admission = new TenantAutomationAdmission(current, value.Proposed, accepted, conversation?.Owner ??
            new(source.Reader.Security.Tenant.TenantId, plan.SessionId, source.Reader.Run.AgentInstanceId, source.Reader.Agent.PrincipalId, source.Reader.Agent.PrincipalId, at),
            graph.Session, graph.Run, source.Reader.Agent.Instance.Revision, plan.ConfigurationHash, ExpectedSessionRevision: conversation?.Snapshot.Revision);
        RequireAdmission(source, value, admission); return admission;
    }

    public static void RequireAdmission(TenantAutomationMutationObservation source, PreparedAutomationMutation value, TenantAutomationAdmission? admission)
    {
        Validate(source, value); var plan = value.Invocation!;
        if (admission is null || admission.Run.SessionId != plan.SessionId || admission.Run.AgentRunId != plan.RunId || admission.Run.ActivationId != plan.ActivationId
            || admission.Run.ResponseId != plan.ResponseId || admission.ConfigurationHash != plan.ConfigurationHash
            || admission.Run.CreatedAtUtc < value.Proposed.Provenance.UpdatedAt
            || admission.Run.Admission.Configuration is null || admission.ExpectedInstanceRevision != source.Reader.Agent.Instance.Revision
            || !TenantAutomationRules.Same(admission.Current, value.Current!) || !TenantAutomationRules.Same(admission.Next, value.Proposed)
            || JsonSerializer.Serialize(admission.Occurrence.ModelPin) != JsonSerializer.Serialize(plan.Model)
            || plan.Conversation is null && (admission.Session.Entries.Count != 1 || admission.Session.Entries[0].EntryId != plan.EntryId))
            throw new SecurityConflictException("automation_plan_changed");
        TenantAutomationRules.Admission(admission, source.Reader.Agent, Requester(source), preparedAt: value.Proposed.Provenance.UpdatedAt);
    }
}
