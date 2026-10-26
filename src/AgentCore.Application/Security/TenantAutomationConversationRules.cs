using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Existing-conversation occurrence admission shares the canonical Session/Run owner.
// The address and snapshot witnesses grant neither Chat nor source-read authority.
public static partial class TenantAutomationConversationRules
{
    public static bool Existing(Automation value) => value.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession;

    public static TenantSession Require(Automation value, TenantSession? target, PreparedAutomationConversation? witness = null)
    {
        if (!Existing(value) || value.CompletionDelivery != AutomationCompletionDelivery.None
            || !TenantCompletionDestinationRules.Eligible(target, value.Owner.TenantId!.Value, value.Owner.AgentInstanceId, value.Owner.ExecutorPrincipalId!.Value)
            || target!.Owner.SessionId != value.ExecutionTarget.SessionId)
            throw new AccessDeniedException("automation_conversation_unavailable");
        if (witness is not null && (witness.SessionRevision < 1 || witness.InputBoundarySequence < 0
            || target.Snapshot.Revision != witness.SessionRevision || target.Snapshot.DurableLastEntrySequence != witness.InputBoundarySequence))
            throw new SecurityConflictException("operation_witness_changed");
        return target;
    }

    public static (string Permission, AccessScope Scope)[] Targets(Automation value) => Existing(value)
        ? TenantCompletionDestinationRules.Communication(value.Owner.TenantId!.Value, value.Owner.AgentInstanceId, value.ExecutionTarget.SessionId!.Value).ToArray()
        : [];

    public static async ValueTask AuthoringAsync(TenantSecurityState state, Guid human, Automation value, TenantSession? target, IamAuthorizer iam, CancellationToken ct)
    {
        if (!Existing(value)) return;
        Require(value, target);
        foreach (var (key, scope) in Targets(value))
            if (!(await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("automation_conversation_unavailable");
    }

    public static async ValueTask<ExecutionModelPin> ModelAsync(AutoModelRouter routing, Automation value, TenantSession target, AgentDefinition definition, IModelCatalog models, CancellationToken ct)
    {
        var preference = ExecutionModelPolicy.MergePreference(target.Snapshot.ModelPreference, value.ModelPreference);
        var selection = await routing.ResolveAsync(models, preference, definition.ModelDefaults, value.RequiresVision, true, "automation",
            new Dictionary<string, string> { ["inputMode"] = "automationExistingSession", ["inputCharacters"] = value.Instructions.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) }, ct);
        return new(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort,
            ExecutionModelSource.ConversationDefault, selection.Binding, selection.Decision);
    }

    public static void RequireSelectable(Automation value, TenantSession target, AgentDefinition definition, IModelCatalog models)
    {
        Require(value, target);
        var preference = ExecutionModelPolicy.MergePreference(
            ExecutionModelPolicy.MergePreference(definition.ModelDefaults, target.Snapshot.ModelPreference), value.ModelPreference);
        ExecutionModelPolicy.RequireSelectable(models, preference);
        if (value.Status == AutomationStatus.Active &&
            ((preference.Mode == "auto" || preference.Reasoning?.Mode == "auto") && !models.RoutingPolicy.Enabled
            || !models.Models.Any(m => m.SupportsInstructionHierarchy && Enum.IsDefined(m.Transport)
                && (preference.Mode != "fixed" || m.OfferingId == preference.OfferingId)
                && (!value.RequiresTools || m.Tools) && (!value.RequiresVision || m.Vision))))
            throw new AccessDeniedException("automation_model_unavailable");
    }

    public static ExecutionModelPin Model(Automation value, TenantSession target, AgentDefinition definition, IModelCatalog models)
    {
        Require(value, target);
        var selection = target.Snapshot.ModelSelection;
        if (selection?.SelectionSource is not (ModelSelectionSource.User or ModelSelectionSource.Host))
            selection = SessionModelBinder.RefreshDefault(models, definition, selection);
        selection ??= SessionModelBinder.PinDefault(models, definition);
        if (value.ModelPreference?.OfferingId is { } key && key != selection.OfferingId
            || value.ModelPreference?.Reasoning?.Effort is { } effort && effort != selection.ReasoningEffort)
            throw new AccessDeniedException("automation_conversation_model_conflict");
        var pin = new ExecutionModelPin(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, ExecutionModelSource.ConversationDefault, selection.Binding);
        if (!ExecutionModelPolicy.Matches(models, pin, out var descriptor)
            || value.RequiresTools && descriptor?.Tools != true || value.RequiresVision && descriptor?.Vision != true)
            throw new AccessDeniedException("automation_model_unavailable");
        return pin;
    }

    public static AgentRun ForOccurrence(TenantSession target, TriggerOccurrence occurrence, TenantAgent agent,
        ResolvedAgentRunConfiguration configuration, Guid activationId, Guid runId, Guid responseId, ExecutionModelPin pin, DateTimeOffset at, Guid? manualInitiator)
    {
        if (occurrence.ExecutionTarget != AutomationExecutionTarget.Existing(target.Owner.SessionId)
            || occurrence.CompletionDelivery != AutomationCompletionDelivery.None || occurrence.AutomationId is null
            || occurrence.Owner != TriggerOwner.ForTenant(target.Owner.TenantId, target.Owner.AgentInstanceId, target.Owner.ExecutorPrincipalId)
            || agent.TenantId != target.Owner.TenantId || agent.PrincipalId != target.Owner.ExecutorPrincipalId
            || agent.Instance.InstanceId != target.Owner.AgentInstanceId
            || !TenantCompletionDestinationRules.Eligible(target, agent.TenantId, agent.Instance.InstanceId, agent.PrincipalId)
            || (occurrence.SourceKind == TriggerSourceKind.ManualInvocation) != (manualInitiator is not null))
            throw new AccessDeniedException("automation_conversation_unavailable");
        var kind = occurrence.SourceKind switch {
            TriggerSourceKind.Schedule => ActivationKind.ScheduledWork,
            TriggerSourceKind.CoreEvent => ActivationKind.CoreEvent,
            TriggerSourceKind.ApplicationEvent => ActivationKind.ApplicationEvent,
            TriggerSourceKind.ManualInvocation => ActivationKind.ManualBackground,
            _ => throw new ArgumentException("Use an exact supported occurrence source.") };
        var trigger = occurrence.SourceKind switch {
            TriggerSourceKind.Schedule => TriggerKind.ScheduledOccurrence,
            TriggerSourceKind.CoreEvent => TriggerKind.CoreEvent,
            TriggerSourceKind.ApplicationEvent => TriggerKind.ApplicationEvent,
            _ => TriggerKind.ManualInvocation };
        // Retain the exact structured occurrence evidence used by the existing
        // Core causation owner; prompt assembly supplies its untrusted-data label.
        var text = occurrence.EvidenceJson;
        var activation = Activation.Start(activationId, target.Owner.SessionId, kind, [], occurrence.SourceEventId, occurrence.OccurrenceId,
            null, null, occurrence.DedupeKey, at, JsonSerializer.Serialize(new AgentRunAdmissionFactory.SignalInput(trigger, text,
                "automation", target.Snapshot.DurableLastEntrySequence)));
        var context = new TrustedExecutionContext(agent.TenantId, agent.PrincipalId, manualInitiator, target.Owner.SessionId,
            runId, activationId, activationId, null, null, occurrence.SourceKind switch {
                TriggerSourceKind.Schedule => ExecutionOrigin.ScheduledOccurrence, TriggerSourceKind.CoreEvent => ExecutionOrigin.CoreEvent,
                TriggerSourceKind.ApplicationEvent => ExecutionOrigin.ApplicationEvent, _ => ExecutionOrigin.ManualInvocation });
        return AgentRun.Create(runId, AgentRunOwner.ForTenant(agent.TenantId, agent.Instance.InstanceId, agent.PrincipalId),
            new(activation, configuration.Configuration.Definition.Id, configuration.Configuration.Definition.Version, configuration.Persona,
                responseId, AgentRunOutputContract.ConversationResponse,
                ExecutionBudgetResolver.Resolve(configuration.Configuration.Definition, configuration.InstanceBudgets, false), configuration.Configuration,
                taskIntent: RunTaskIntents.FromOccurrence(occurrence), executionContext: context),
            new(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort, pin.Binding, pin.Decision), AgentRunLimits.DefaultMaxAttempts, at,
            configuration.Skills, configuration.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
    }
}
