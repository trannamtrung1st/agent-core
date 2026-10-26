using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Triggers;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Execution;

/// <summary>Freezes one effective model turn; the caller commits its input graph through IAgentRunStore.</summary>
public static class AgentRunAdmissionFactory
{
    public static bool RequestsCleanup(string text) => BrowserCleanupIntentRecognition.From(text).Any;

    internal static EffectiveExecutionBudget WithCleanupIntent(EffectiveExecutionBudget budget, string text)
    {
        var intent = BrowserCleanupIntentRecognition.From(text);
        return budget with { RequestedCleanup = intent.Any, CleanupIntent = intent };
    }

    public sealed record SignalInput(TriggerKind TriggerKind, string? Text, string? EnvironmentKind, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? InputBoundarySequence = null);

    public static AgentRun ForConfigurationContinuation(Guid activationId, Guid runId, Guid responseId,
        SessionSnapshot snapshot, AgentRun source, ConfigurationPendingTask task, DateTimeOffset now,
        IReadOnlyList<EffectiveSkill> catalog, EffectiveExecutionBudget budget, AgentRunConfiguration configuration, bool emailContinuation = false, EmailAccountSelection? emailSelection = null)
    {
        var text = $"Continue verified configuration task: {task.Goal}\nPending task: {source.AgentRunId:D}. "
            + $"The configuration change committed at Instance revision {task.InstanceRevision}. This is one bounded fresh Run. "
            + $"Use {task.Target} to finish only this diagnosed task. Do not repeat configuration changes or prior effects. Any write requires a new exact owner approval.";
        if (emailContinuation) text = $"Continue the owner's email task after confirmed account access: {task.Goal}\n"
            + $"Source Run: {source.AgentRunId:D}. This fresh Run uses newly admitted bindings. Use {task.Target} for this task only. "
            + "Never repeat prior effects or configuration changes. Every write needs a new exact owner approval.\n"
            + "Owner-selected account (JSON identity data): " + JsonSerializer.Serialize(new { accountRef = emailSelection?.SelectedAccountRef })
            + "\nUse this accountRef for the email task. It is a selection hint only; Core still requires admitted bindings and live account authorization.";
        var activation = new Activation(activationId, snapshot.SessionId, ActivationKind.ConfigurationContinuation,
            [], source.ActivationId, null, snapshot.SessionId, source.AgentRunId,
            $"configuration-continuation:{source.AgentRunId:D}", now, source.Admission.Activation.RootActivationId, source.ActivationId, JsonSerializer.Serialize(new { TriggerKind = TriggerKind.UserTurn, Text = text, EnvironmentKind = emailContinuation ? "email-account" : "configuration", ContinuationTarget = task.Target, InputBoundarySequence = snapshot.DurableLastEntrySequence, SelectedAccountId = emailSelection?.SelectedAccountId, SelectedAccountRef = emailSelection?.SelectedAccountRef }));
        var selection = snapshot.ModelSelection ?? throw AgentCoreErrors.Validation("Continuation requires a resolved model.");
        var context = source.Admission.ExecutionContext is { } previous ? new TrustedExecutionContext(previous.TenantId, previous.AgentPrincipalId,
            previous.OriginalInitiatorPrincipalId, source.SessionId, runId, activationId, previous.RootActivationId, source.AgentRunId, source.ActivationId, ExecutionOrigin.ConfigurationContinuation) : null;
        return AgentRun.Create(runId, source.Owner, new(activation, configuration.Definition.Id, configuration.Definition.Version,
            snapshot.PinnedPersona!, responseId, AgentRunOutputContract.ConversationResponse, budget, configuration, executionContext: context),
            new(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, selection.Binding, selection.Decision),
            1, now, catalog, catalog.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
    }

    public static AgentRun ForAdmittedSignal(Guid activationId, Guid runId, Guid responseId, SessionSnapshot snapshot,
        AgentTrigger trigger, DateTimeOffset atUtc, IReadOnlyList<EffectiveSkill> catalog, ExecutionModelPin? occurrenceModel = null, EffectiveExecutionBudget? budget = null, AgentRunConfiguration? configuration = null)
    {
        if (budget is not null) budget = WithCleanupIntent(budget, trigger.Text ?? "");
        var kind = trigger.Kind switch
        {
            TriggerKind.ScheduledOccurrence => ActivationKind.ScheduledWork,
            TriggerKind.CoreEvent => ActivationKind.CoreEvent,
            TriggerKind.ApplicationEvent => ActivationKind.ApplicationEvent,
            TriggerKind.ManualInvocation => ActivationKind.ManualBackground,
            _ => ActivationKind.Initiative
        };
        var activation = Activation.Start(activationId, snapshot.SessionId, kind, [], trigger.EventId,
            ToolResources.IsOccurrence(trigger.Kind) ? trigger.EventId : null, null, null,
            $"signal:{trigger.EventId:D}", atUtc, JsonSerializer.Serialize(new SignalInput(trigger.Kind, trigger.Text, trigger.EnvironmentKind)));
        var model = occurrenceModel is { } pin
            ? new AgentRunModelPin(pin.OfferingId, pin.ConnectionId, pin.ExternalModelId, pin.ReasoningEffort, pin.Binding, pin.Decision)
            : snapshot.ModelSelection is { } selection
                ? new AgentRunModelPin(selection.OfferingId, selection.ConnectionId, selection.ExternalModelId, selection.ReasoningEffort, selection.Binding, selection.Decision)
                : throw AgentCoreErrors.Validation("AgentRun requires a resolved model.");
        return AgentRun.Create(runId, new(snapshot.AgentInstanceId, snapshot.ProfileId
            ?? throw AgentCoreErrors.Validation("AgentRun requires a trusted profile.")),
            new(activation, snapshot.Definition.Id, snapshot.Definition.Version,
                snapshot.PinnedPersona ?? throw AgentCoreErrors.Validation("AgentRun requires a pinned persona."), responseId, AgentRunOutputContract.ConversationResponse, budget, configuration ?? new(snapshot.Definition, 0, snapshot.PinnedPersonaRevision ?? 0, [])),
            model, AgentRunLimits.DefaultMaxAttempts, atUtc, catalog,
            catalog.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray());
    }

    public static AgentRun ForAcceptedUserBatch(Guid activationId, Guid agentRunId, Guid responseId,
        SessionSnapshot snapshot, IReadOnlyList<ConversationEntry> users, DateTimeOffset admittedAtUtc,
        IReadOnlyList<EffectiveSkill> catalog, EffectiveExecutionBudget? budget = null, AgentRunConfiguration? configuration = null, ComposerRunInput? composerInput = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(users);
        ArgumentNullException.ThrowIfNull(catalog);
        if (users.Count is < 1 or > Activation.MaxSourceEntries
            || users.Any(entry => entry.Role != ConversationRole.User || entry.Status != EntryStatus.Completed)
            || users.Select(entry => entry.EntryId).Distinct().Count() != users.Count
            || users.Zip(users.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw AgentCoreErrors.Validation("A turn requires an ordered, bounded batch of accepted user entries.");
        foreach (var user in users)
        {
            if (snapshot.Entries.SingleOrDefault(entry => entry.EntryId == user.EntryId) != user)
                throw AgentCoreErrors.Validation("User inputs must belong to the admitted Session snapshot.");
        }
        var instanceId = snapshot.AgentInstanceId;
        if (snapshot.ProfileId is not { } profileId || profileId == Guid.Empty)
            throw AgentCoreErrors.Validation("AgentRun admission requires an Agent Instance and trusted profile.");
        var model = snapshot.ModelSelection
            ?? throw AgentCoreErrors.Validation("AgentRun admission requires a resolved model.");
        var persona = snapshot.PinnedPersona
            ?? throw AgentCoreErrors.Validation("AgentRun admission requires a pinned persona.");
        // Stable across crash/replay even if the proposal receives fresh execution IDs.
        var batchKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(',', users.Select(entry => entry.EntryId.ToString("D")))))).ToLowerInvariant();
        var activation = Activation.Start(activationId, snapshot.SessionId, ActivationKind.UserTurn,
            users.Select(entry => entry.EntryId).ToArray(), users[0].SourceEventId ?? users[0].EntryId,
            null, null, null, $"user-batch:{batchKey}", admittedAtUtc);
        if (budget is not null) budget = WithCleanupIntent(budget, string.Join("\n", users.Select(user => user.Text)));
        return AgentRun.Create(agentRunId, new AgentRunOwner(instanceId, profileId),
            new AgentRunAdmission(activation, snapshot.Definition.Id, snapshot.Definition.Version, persona, responseId, AgentRunOutputContract.ConversationResponse, budget, configuration ?? new(snapshot.Definition, 0, snapshot.PinnedPersonaRevision ?? 0, []), composerInput),
            new AgentRunModelPin(model.OfferingId, model.ConnectionId, model.ExternalModelId, model.ReasoningEffort, model.Binding, model.Decision),
            AgentRunLimits.DefaultMaxAttempts, admittedAtUtc,
            pinnedSkillCatalog: catalog,
            activeSkillKeys: composerInput?.Error is not null ? catalog.Where(skill => skill.Projection == SkillProjection.Always).Select(skill => skill.Key).ToArray()
                : AgentCore.Application.Composer.ComposerInputRules.ActiveSkills(snapshot.Definition, catalog,
                    users.SelectMany(user => UserMessageContent.ExplicitSkills(user.Parts))));
    }
}
