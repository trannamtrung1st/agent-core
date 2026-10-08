using System.Globalization;
using System.IO;
using System.Text.Json;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Triggers;

public enum TriggerAuthorizationClassification
{
    CurrentUserTurn,
    UnrelatedUserTurn,
    Historical,
    Memory,
    Initiative,
    Environment,
    Occurrence,
    Missing
}

[Flags]
public enum TriggerCommandAction
{
    None = 0,
    Create = 1,
    List = 2,
    Update = 4,
    Cancel = 8
}

public readonly record struct TriggerAuthorizationResult(
    TriggerAuthorizationClassification Classification,
    TriggerCommandAction AllowedActions);

public sealed record PendingTriggerProposal(string ToolName, string ArgumentsJson);

public sealed record TriggerCommandContext(
    TriggerOwner? Owner,
    Guid SessionId,
    string? ProfileTimeZoneId,
    string? CurrentUserText,
    string? ConversationLanguage,
    TriggerAuthorizationClassification Classification,
    TriggerCommandAction AllowedActions,
    bool ExecutePendingProposal,
    PendingTriggerProposal? PendingProposal,
    Guid? SourceEventId,
    DateTimeOffset UtcNow,
    ScheduleConversationContext? ScheduleContext = null,
    ScheduleDraftContext? ScheduleDraft = null);

public static class TriggerAuthorization
{
    public static TriggerAuthorizationResult Classify(
        TriggerKind kind,
        string? currentUserText,
        PendingTriggerProposal? pendingProposal,
        ITriggerCommandAuthorizer authorizer,
        string? conversationLanguage,
        ScheduleConversationContext? scheduleContext = null)
    {
        if (kind is TriggerKind.ScheduledOccurrence or TriggerKind.ApplicationEvent)
        {
            return new(TriggerAuthorizationClassification.Occurrence, TriggerCommandAction.None);
        }

        if (kind == TriggerKind.EnvironmentUpdate)
        {
            return new(TriggerAuthorizationClassification.Environment, TriggerCommandAction.None);
        }

        if (kind is TriggerKind.LongSilence or TriggerKind.UnfinishedInteraction)
        {
            return new(TriggerAuthorizationClassification.Initiative, TriggerCommandAction.None);
        }

        if (TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(currentUserText, conversationLanguage, scheduleContext))
        {
            return new(TriggerAuthorizationClassification.CurrentUserTurn, TriggerCommandAction.None);
        }

        if (pendingProposal is not null && authorizer.IsScheduleConfirmation(currentUserText, conversationLanguage))
        {
            return new(TriggerAuthorizationClassification.CurrentUserTurn, ActionForTool(pendingProposal.ToolName));
        }

        return new(TriggerAuthorizationClassification.UnrelatedUserTurn, TriggerCommandAction.None);
    }

    public static bool IsConfirmationTurn(
        string? currentUserText,
        bool hasPendingProposal,
        ITriggerCommandAuthorizer authorizer,
        string? conversationLanguage) =>
        hasPendingProposal
        && !IsExplicitScheduleRequest(currentUserText, authorizer, conversationLanguage)
        && authorizer.IsScheduleConfirmation(currentUserText, conversationLanguage);

    public static bool IsExplicitScheduleRequest(
        string? text,
        ITriggerCommandAuthorizer authorizer,
        string? conversationLanguage) =>
        TriggerScheduleTurnPreflight.IsScheduleRelatedTurn(text, conversationLanguage, scheduleContext: null);

    public static TriggerCommandAction ActionForTool(string toolName) => toolName switch
    {
        ToolCatalog.AutomationList => TriggerCommandAction.List,
        ToolCatalog.AutomationUpdate => TriggerCommandAction.Update,
        ToolCatalog.AutomationDelete => TriggerCommandAction.Cancel,
        ToolCatalog.AutomationCreate => TriggerCommandAction.Create,
        _ => TriggerCommandAction.None
    };

}

public static class TriggerScheduleCommands
{
    private static readonly ITriggerCommandAuthorizer DefaultAuthorizer = new HeuristicTriggerCommandAuthorizer();

    public static async Task<ToolExecutionResult> ExecuteAsync(
        AgentDefinition definition,
        IAutomationService? registrations,
        string toolName,
        JsonElement arguments,
        TriggerCommandContext? command,
        CancellationToken cancellationToken,
        ITriggerCommandAuthorizer? authorizer = null,
        IAgentInstanceStore? instances = null,
        IAgentDefinitionStore? definitions = null,
        IMemoryStore? profiles = null,
        AdminAutomationAuthoringService? automationAuthoring = null)
    {
        authorizer ??= DefaultAuthorizer;
        if (registrations is null)
        {
            return Result("unavailable", "Scheduling is unavailable.", clearProposal: true);
        }

        var context = command ?? new TriggerCommandContext(
            null,
            Guid.Empty,
            null,
            null,
            null,
            TriggerAuthorizationClassification.Missing,
            TriggerCommandAction.None,
            false,
            null,
            null,
            DateTimeOffset.UnixEpoch);
        var operation = Operation(toolName);
        var effectiveName = toolName;
        var effectiveArguments = arguments;
        if (context.ExecutePendingProposal)
        {
            if (context.PendingProposal is null)
            {
                return Result("forbidden", "There is no schedule proposal to confirm.", clearProposal: true);
            }

            effectiveName = context.PendingProposal.ToolName;
            try
            {
                using var document = JsonDocument.Parse(context.PendingProposal.ArgumentsJson);
                effectiveArguments = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Result("validation", "The pending schedule proposal is invalid.", clearProposal: true);
            }
        }

        var required = TriggerAuthorization.ActionForTool(effectiveName);
        var authorization = await ResolveAuthorizationAsync(context, authorizer, required, cancellationToken).ConfigureAwait(false);
        if (required == TriggerCommandAction.None || authorization != TriggerCommandAuthorizationDecision.Allow)
        {
            if (context.Classification is TriggerAuthorizationClassification.Initiative or TriggerAuthorizationClassification.Environment
                && !context.ExecutePendingProposal
                && SchedulingEnabled(definition.TriggerPolicy))
            {
                RuntimeTelemetry.RecordAutomation(operation, "confirmation_required");
                return Confirmation(toolName, arguments);
            }

            if (authorization == TriggerCommandAuthorizationDecision.Ambiguous
                && context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                    or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordAutomation(operation, "authorization_ambiguous");
                return Result(
                    "authorization_ambiguous",
                    "The current message does not clearly authorize this schedule action. Ask what the user wants; do not retry different time argument shapes.",
                    clearProposal: false);
            }

            if (authorization == TriggerCommandAuthorizationDecision.ClassifierUnavailable
                && context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                    or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordAutomation(operation, "authorization_classifier_unavailable");
                return Result(
                    "authorization_classifier_unavailable",
                    "Schedule authorization is temporarily unavailable. Ask the user to restate the request or try again shortly.",
                    clearProposal: false);
            }

            if (context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordAutomation(operation, "authorization_denied");
                return Result(
                    "authorization_denied",
                    "The current user message does not authorize this schedule action. Clarify the request or use the correct schedule command.",
                    clearProposal: false);
            }

            RuntimeTelemetry.RecordAutomation(operation, "forbidden");
            return Result(
                "forbidden",
                "This schedule action is not permitted for the current trigger.",
                clearProposal: true);
        }

        if (context.Owner is not TriggerOwner owner)
        {
            return Result("validation", "Schedule owner is required.", clearProposal: true);
        }

        var policyDefinition = definition;
        var existing = required == TriggerCommandAction.Update
            ? await registrations.GetAsync(owner, RequireId(effectiveArguments), cancellationToken) : null;
        var eventTrigger = effectiveArguments.TryGetProperty("eventId", out _) || existing?.Trigger is EventTrigger && !HasScheduleFields(effectiveArguments);
        var sourceKind = eventTrigger ? TriggerSourceKind.ApplicationEvent : TriggerSourceKind.Schedule;
        if (effectiveArguments.TryGetProperty("eventId", out _) && HasScheduleFields(effectiveArguments))
            return Result("validation", "Choose exactly one Schedule or Event trigger.", clearProposal: false);
        var durablePolicyConfigured = instances is not null && definitions is not null && profiles is not null;
        if (durablePolicyConfigured)
        {
            if (required is TriggerCommandAction.Create or TriggerCommandAction.Update)
            {
                var admission = await TriggerDurableSchedulingPolicy.EvaluateUserSchedulingAsync(
                        owner,
                        instances!,
                        definitions!,
                        profiles!,
                        cancellationToken, sourceKind)
                    .ConfigureAwait(false);
                if (!admission.Allowed)
                {
                    RuntimeTelemetry.RecordAutomation(operation, "policy");
                    return Result(
                        "policy",
                        TriggerDurableSchedulingPolicy.PolicyMessage(admission.DenialReason!.Value),
                        clearProposal: false);
                }

                policyDefinition = admission.Definition!;
            }
        }
        else if (required is TriggerCommandAction.Create or TriggerCommandAction.Update
                 && !(OccurrenceCompatibility.Allows(definition, sourceKind) && definition.TriggerPolicy is { Enabled: true, AllowUserScheduling: true }))
        {
            RuntimeTelemetry.RecordAutomation(operation, "policy");
            return Result(
                "policy",
                "Scheduling is disabled for this agent.",
                clearProposal: false);
        }

        try
        {
            var json = effectiveName switch
            {
                ToolCatalog.AutomationCreate when effectiveArguments.TryGetProperty("eventId", out _) => automationAuthoring is null
                    ? Error("unavailable", "Automation authoring is unavailable.")
                    : RegistrationJson(await automationAuthoring.SaveAsync(owner.AgentInstanceId, null, 0, true,
                        TryString(effectiveArguments, "name", out var eventName) ? eventName : RequireInstructions(effectiveArguments)[..Math.Min(80, RequireInstructions(effectiveArguments).Length)],
                        RequireInstructions(effectiveArguments), new EventTrigger(Guid.Parse(effectiveArguments.GetProperty("eventId").GetString() ?? "")),
                        TryString(effectiveArguments, "modelKey", out var eventModel) ? eventModel : null,
                        TryString(effectiveArguments, "reasoningEffort", out var eventEffort) ? eventEffort : null, cancellationToken,
                        new(TriggerAuthorizationOrigin.CurrentUserTurn, context.SessionId, context.SourceEventId, context.UtcNow, context.UtcNow),
                        executionTarget: ChatTarget(effectiveArguments, context), completionDelivery: ChatDelivery(effectiveArguments, context), requiresTools: OptionalRequirement(effectiveArguments, "requiresTools"), requiresVision: OptionalRequirement(effectiveArguments, "requiresVision"))),
                ToolCatalog.AutomationCreate when automationAuthoring is not null => await CreateScheduleAutomationAsync(
                    policyDefinition, automationAuthoring, owner, context, effectiveArguments, cancellationToken),
                ToolCatalog.AutomationCreate => effectiveArguments.TryGetProperty("kind", out var recurrence) && recurrence.GetString() is "daily" or "weekly" or "fixed_interval"
                    ? await CreateRecurringAsync(policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false)
                    : await CreateOnceAsync(policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.AutomationList => await ListAsync(
                    policyDefinition, registrations, owner, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.AutomationUpdate => await UpdateAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken, automationAuthoring).ConfigureAwait(false),
                ToolCatalog.AutomationDelete => await CancelAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken, automationAuthoring).ConfigureAwait(false),
                _ => Error("forbidden", "Tool is not permitted for this role.")
            };
            return new ToolExecutionResult(json, ReplaceTriggerProposal: true, TriggerProposal: null);
        }
        catch (AgentCoreException exception)
        {
            var code = exception.StatusCode switch
            {
                404 => "not_found",
                409 => "conflict",
                403 => "forbidden",
                _ => "validation"
            };
            return Result(code, exception.Message, clearProposal: false);
        }
        catch (TriggerScheduleCommandException exception)
        {
            return Result(
                exception.ErrorCode,
                exception.Message,
                clearProposal: false,
                exception.Draft);
        }
        catch (ArgumentException exception)
        {
            return Result("schedule_validation_failed", exception.Message, clearProposal: false);
        }
        catch (TriggerTimeZoneUnavailableException)
        {
            return Result("schedule_validation_failed", "Timezone is unavailable. Ask the user for a different timezone.", clearProposal: false);
        }
    }

    private static async Task<string> CreateScheduleAutomationAsync(AgentDefinition definition,
        AdminAutomationAuthoringService authoring, TriggerOwner owner, TriggerCommandContext context,
        JsonElement arguments, CancellationToken ct)
    {
        var policy = RequirePolicy(definition, "create", requireUserScheduling: true);
        var merged = MergeDraftArguments(arguments, context);
        var instructions = RequireInstructions(merged);
        TriggerSchedule schedule = merged.TryGetProperty("kind", out var kind) && kind.GetString() is "daily" or "weekly" or "fixed_interval"
            ? ResolveRecurring(merged, context, policy) : ResolveOneShot(merged, context, policy).Schedule;
        return RegistrationJson(await authoring.SaveAsync(owner.AgentInstanceId, null, 0, true,
            TryString(arguments, "name", out var name) ? name : instructions[..Math.Min(80, instructions.Length)],
            instructions, new ScheduleTrigger(schedule),
            TryString(arguments, "modelKey", out var model) ? model : null,
            TryString(arguments, "reasoningEffort", out var effort) ? effort : null, ct,
            new(TriggerAuthorizationOrigin.CurrentUserTurn, context.SessionId, context.SourceEventId, context.UtcNow, context.UtcNow),
                        executionTarget: ChatTarget(arguments, context), completionDelivery: ChatDelivery(arguments, context), requiresTools: OptionalRequirement(arguments, "requiresTools"), requiresVision: OptionalRequirement(arguments, "requiresVision")));
    }

    private static async Task<string> CreateOnceAsync(
        AgentDefinition definition,
        IAutomationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var policy = RequirePolicy(definition, "create", requireUserScheduling: true);
        await RequireCapacityAsync(registrations, owner, policy, cancellationToken).ConfigureAwait(false);
        var intent = RequireInstructions(arguments);
        var (schedule, next) = ResolveOneShot(arguments, context, policy);
        var created = await registrations.CreateAsync(
            new AutomationDraft(
                owner,
                intent,
                schedule,
                next,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                context.SessionId,
                context.SourceEventId,
                TryString(arguments, "name", out var name) ? name : null),
            cancellationToken).ConfigureAwait(false);
        return RegistrationJson(created);
    }

    private static async Task<string> CreateRecurringAsync(
        AgentDefinition definition,
        IAutomationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var policy = RequirePolicy(definition, "create", requireUserScheduling: true);
        await RequireCapacityAsync(registrations, owner, policy, cancellationToken).ConfigureAwait(false);
        var merged = MergeDraftArguments(arguments, context);
        var intent = RequireInstructions(merged);
        var schedule = ResolveRecurring(merged, context, policy);
        var next = TriggerScheduleCalculator.InitialNext(schedule, context.UtcNow)
            ?? throw new ArgumentException("No future occurrence matches this schedule.");
        var created = await registrations.CreateAsync(
            new AutomationDraft(
                owner,
                intent,
                schedule,
                next,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                context.SessionId,
                context.SourceEventId,
                TryString(arguments, "name", out var name) ? name : null),
            cancellationToken).ConfigureAwait(false);
        return RegistrationJson(created);
    }

    private static async Task<string> ListAsync(
        AgentDefinition definition,
        IAutomationService registrations,
        TriggerOwner owner,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        RequirePolicy(definition, "list", requireUserScheduling: false);
        AutomationStatus? status = null;
        if (TryString(arguments, "status", out var statusText) && !string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<AutomationStatus>(statusText, ignoreCase: true, out var parsed))
            {
                throw new ArgumentException("Schedule status is invalid.");
            }

            status = parsed;
        }

        var rows = await registrations.ListAsync(owner, status, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            automations = rows.Select(Projection).ToArray()
        });
    }

    private static async Task<string> UpdateAsync(
        AgentDefinition definition,
        IAutomationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken,
        AdminAutomationAuthoringService? authoring)
    {
        var policy = definition.TriggerPolicy ?? throw new ArgumentException("Automation is disabled.");
        var id = RequireId(arguments);
        var current = await registrations.GetAsync(owner, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        var expected = ResolveExpectedRevision(arguments, context, current);
        var hasIntent = arguments.TryGetProperty("instructions", out _);
        var hasSchedule = HasScheduleFields(arguments);
        if (authoring is not null)
        {
            var trigger = current.Trigger;
            if (arguments.TryGetProperty("eventId", out var eventId))
                trigger = new EventTrigger(Guid.Parse(eventId.GetString() ?? ""));
            else if (hasSchedule)
            {
                TriggerSchedule schedule;
                if (current.Trigger is ScheduleTrigger { Schedule: FixedIntervalSchedule fixedCurrent } && !IsExplicitCalendarKindChange(arguments))
                    (schedule, _) = ApplyFixedIntervalUpdate(fixedCurrent, arguments, context, policy);
                else if (current.Trigger is ScheduleTrigger { Schedule: OneShotSchedule } || HasOneShotFields(arguments))
                    (schedule, _) = ResolveOneShot(arguments, context, policy);
                else schedule = ResolveRecurring(arguments, context, policy);
                trigger = new ScheduleTrigger(schedule);
            }
            var name = arguments.TryGetProperty("name", out var n) ? n.GetString() ?? "" : current.Name;
            var instructions = hasIntent ? RequireInstructions(arguments) : current.Instructions;
            var model = arguments.TryGetProperty("modelKey", out var m) ? m.GetString() : current.ModelOverrideCatalogKey;
            var effort = arguments.TryGetProperty("reasoningEffort", out var e) ? e.GetString() : current.ModelOverrideReasoningEffort;
            return RegistrationJson(await authoring.SaveAsync(owner.AgentInstanceId, id, expected,
                current.Status == AutomationStatus.Active, name, instructions, trigger, model, effort, cancellationToken,
                executionTarget: ChatTarget(arguments, context, current), completionDelivery: ChatDelivery(arguments, context, current), requiresTools: OptionalRequirement(arguments, "requiresTools"), requiresVision: OptionalRequirement(arguments, "requiresVision")));
        }
        AutomationChange change;
        if (!hasIntent && !hasSchedule)
        {
            throw new ArgumentException("Update requires an intent or a schedule change.");
        }

        if (hasSchedule)
        {
            TriggerSchedule schedule;
            DateTimeOffset? next;
            if (current.Schedule is FixedIntervalSchedule fixedCurrent && !IsExplicitCalendarKindChange(arguments))
            {
                (schedule, next) = ApplyFixedIntervalUpdate(fixedCurrent, arguments, context, policy);
            }
            else if (current.Schedule is OneShotSchedule || HasOneShotFields(arguments))
            {
                (schedule, next) = ResolveOneShot(arguments, context, policy);
            }
            else
            {
                schedule = ResolveRecurring(arguments, context, policy);
                next = TriggerScheduleCalculator.InitialNext(schedule, context.UtcNow)
                    ?? throw new ArgumentException("No future occurrence matches this schedule.");
            }

            if (schedule is not OneShotSchedule && next is null)
            {
                next = TriggerScheduleCalculator.InitialNext(schedule, context.UtcNow)
                    ?? throw new ArgumentException("No future occurrence matches this schedule.");
            }

            change = AutomationChange.ScheduleOnly(schedule, next, null);
            if (hasIntent)
            {
                change = AutomationChange.Full(RequireInstructions(arguments), schedule, next, null);
            }
        }
        else
        {
            change = AutomationChange.InstructionsOnly(RequireInstructions(arguments));
        }

        var updated = await registrations.UpdateAsync(owner, id, expected, change, cancellationToken).ConfigureAwait(false);
        return RegistrationJson(updated);
    }

    private static async Task<string> CancelAsync(
        AgentDefinition definition,
        IAutomationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken,
        AdminAutomationAuthoringService? automationAuthoring = null)
    {
        RequirePolicy(definition, "cancel", requireUserScheduling: false);
        var id = RequireId(arguments);
        var current = await registrations.GetAsync(owner, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Automation was not found.");
        if (automationAuthoring is not null)
        {
            await automationAuthoring.DeleteAsync(owner.AgentInstanceId, id, ResolveExpectedRevision(arguments, context, current), cancellationToken);
            return RegistrationJson((await registrations.GetAsync(owner, id, cancellationToken))!);
        }
        var cancelled = await registrations.CancelAsync(
            owner,
            id,
            ResolveExpectedRevision(arguments, context, current),
            cancellationToken).ConfigureAwait(false);
        return RegistrationJson(cancelled);
    }

    private static (TriggerSchedule Schedule, DateTimeOffset Next) ResolveOneShot(
        JsonElement arguments,
        TriggerCommandContext context,
        TriggerPolicy policy)
    {
        var now = TriggerScheduleCalculator.Truncate(context.UtcNow);
        var hasOffset = TryPresent(arguments, "relativeDayOffset", out var offsetElement);
        var hasDate = TryTimeString(arguments, "localDate", out var localDateText);
        var hasAt = TryTimeString(arguments, "atUtc", out var atText);
        var hasDelay = TryPresent(arguments, "relativeDelaySeconds", out var delayElement);
        if (hasDelay
            && hasOffset
            && !hasDate
            && !hasAt
            && TryWholeNumber(offsetElement, out var unusedOffset)
            && unusedOffset == 0
            && !TryTimeString(arguments, "localTime", out _))
        {
            hasOffset = false;
        }

        var forms = (hasOffset ? 1 : 0) + (hasDate ? 1 : 0) + (hasAt ? 1 : 0) + (hasDelay ? 1 : 0);
        if (forms != 1)
        {
            var present = new List<string>(4);
            if (hasDelay)
            {
                present.Add("relativeDelaySeconds");
            }

            if (hasOffset)
            {
                present.Add("relativeDayOffset");
            }

            if (hasDate)
            {
                present.Add("localDate");
            }

            if (hasAt)
            {
                present.Add("atUtc");
            }

            throw new ArgumentException(
                "Schedule time is missing or ambiguous. Present: "
                + (present.Count == 0 ? "none" : string.Join(',', present))
                + ". Use exactly one of relativeDelaySeconds, relativeDayOffset with localTime, localDate with localTime, or atUtc. Ask the user to restate the full request with a clear time. Do not ask for a bare yes/no confirmation.");
        }

        DateTimeOffset instant;
        DateOnly? localDate = null;
        TimeOnly? localTime = null;
        string zone;
        if (hasOffset)
        {
            zone = RequireTimeZone(arguments, context);
            if (!TryWholeNumber(offsetElement, out var offset))
            {
                throw new ArgumentException("relativeDayOffset must be a whole number of days.");
            }

            if (offset < 0 || offset > policy.OneShotHorizonDays)
            {
                throw new ArgumentException("One-shot day offset is outside the scheduling horizon.");
            }

            localTime = RequireLocalTime(arguments);
            var createdLocal = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TriggerScheduleCalculator.RequireZone(zone)).DateTime);
            localDate = createdLocal.AddDays(offset);
            instant = TriggerScheduleCalculator.ResolveWallClock(zone, localDate.Value, localTime.Value);
        }
        else if (hasDate)
        {
            zone = RequireTimeZone(arguments, context);
            localDate = ParseDate(localDateText, "localDate");
            localTime = RequireLocalTime(arguments);
            instant = TriggerScheduleCalculator.ResolveWallClock(zone, localDate.Value, localTime.Value);
        }
        else if (hasDelay)
        {
            if (!TryWholeNumber(delayElement, out var seconds))
            {
                throw new ArgumentException("relativeDelaySeconds must be a whole number of seconds.");
            }

            var maxSeconds = policy.OneShotHorizonDays * 86_400;
            if (seconds < 1 || seconds > maxSeconds)
            {
                throw new ArgumentException("relativeDelaySeconds is outside the scheduling horizon.");
            }

            instant = now.AddSeconds(seconds);
            zone = OptionalTimeZoneForDisplay(arguments, context);
            var zoned = TimeZoneInfo.ConvertTime(instant, TriggerScheduleCalculator.RequireZone(zone));
            localDate = DateOnly.FromDateTime(zoned.DateTime);
            localTime = TimeOnly.FromDateTime(zoned.DateTime);
        }
        else
        {
            instant = ParseUtc(atText, "atUtc");
            zone = OptionalTimeZoneForDisplay(arguments, context);
        }

        instant = TriggerScheduleCalculator.Truncate(instant);
        var schedule = new OneShotSchedule(instant, zone, localDate, localTime);
        ScheduleDefinitionPolicy.Validate(schedule, policy, now);
        return (schedule, instant);
    }

    private static TriggerSchedule ResolveRecurring(
        JsonElement arguments,
        TriggerCommandContext context,
        TriggerPolicy policy)
    {
        if (arguments.TryGetProperty("intervalSeconds", out _))
        {
            var kindText = TryString(arguments, "kind", out var kindProbe) ? kindProbe : null;
            if (kindText is null
                || (!kindText.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
                    && !kindText.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase)))
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Sub-day fixed intervals require kind fixed_interval with intervalSeconds. Daily and weekly schedules are calendar-based only.");
            }
        }

        var kind = RequireString(arguments, "kind");
        int? cap = arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null
            ? capElement.GetInt32()
            : null;

        if (kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase))
        {
            if (!arguments.TryGetProperty("intervalSeconds", out var secondsElement)
                || secondsElement.ValueKind != JsonValueKind.Number
                || !secondsElement.TryGetInt32(out var intervalSeconds))
            {
                throw new TriggerScheduleCommandException(
                    "schedule_validation_failed",
                    "Fixed-interval schedules require intervalSeconds.");
            }

            var intent = TryString(arguments, "instructions", out var intentText) ? intentText : string.Empty;
            ScheduleDefinitionPolicy.RequireInterval(policy, intervalSeconds, context.UtcNow, intent);

            if (intervalSeconds > TriggerLimits.MaxFixedIntervalSeconds)
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Fixed interval is longer than the supported maximum.");
            }

            var endAt = ResolveOptionalEndAtUtc(arguments);

            var anchor = TriggerScheduleCalculator.Truncate(context.UtcNow);
            var schedule = new FixedIntervalSchedule(intervalSeconds, anchor, endAt, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, context.UtcNow, intent);
            return schedule;
        }

        var zone = RequireTimeZone(arguments, context);
        var localTime = RequireLocalTime(arguments);
        var interval = arguments.TryGetProperty("interval", out var intervalElement) && intervalElement.TryGetInt32(out var parsed)
            ? parsed
            : 1;
        var start = OptionalDate(arguments, "startDate");
        var end = OptionalDate(arguments, "endDate");
        if (kind.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            var schedule = new DailySchedule(interval, localTime, zone, start, end, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, context.UtcNow);
            return schedule;
        }

        if (kind.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            var weekdays = ReadWeekdays(arguments);
            var schedule = new WeeklySchedule(interval, weekdays, localTime, zone, start, end, cap);
            ScheduleDefinitionPolicy.Validate(schedule, policy, context.UtcNow);
            return schedule;
        }

        throw new TriggerScheduleCommandException(
            "unsupported_recurrence",
            "Schedule kind must be fixed_interval, daily, or weekly.");
    }

    private static JsonElement MergeDraftArguments(JsonElement arguments, TriggerCommandContext context)
    {
        if (context.ScheduleDraft is not { IsActive: true } draft)
        {
            return arguments;
        }

        using var document = JsonDocument.Parse(arguments.GetRawText());
        var root = document.RootElement;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in root.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            if (!root.TryGetProperty("instructions", out var intentElement)
                || intentElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(intentElement.GetString()))
            {
                writer.WriteString("instructions", draft.Instructions);
            }

            if (draft.RecurrenceKind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("kind", out _))
                {
                    writer.WriteString("kind", "fixed_interval");
                }

                if (!root.TryGetProperty("intervalSeconds", out _))
                {
                    var seconds = ScheduleIntervalLanguage.TryParseIntervalSeconds(context.CurrentUserText)
                        ?? draft.IntervalSeconds;
                    if (seconds is int parsed)
                    {
                        writer.WriteNumber("intervalSeconds", parsed);
                    }
                }
            }

            writer.WriteEndObject();
        }

        using var merged = JsonDocument.Parse(stream.ToArray());
        return merged.RootElement.Clone();
    }

    private static async Task RequireCapacityAsync(
        IAutomationService registrations,
        TriggerOwner owner,
        TriggerPolicy policy,
        CancellationToken cancellationToken)
    {
        var active = await registrations.CountActiveAsync(owner, cancellationToken).ConfigureAwait(false);
        if (active >= policy.MaxActiveRegistrations)
        {
            throw new ArgumentException("Active schedule limit has been reached.");
        }
    }

    private static TriggerPolicy RequirePolicy(AgentDefinition definition, string operation, bool requireUserScheduling)
    {
        var policy = definition.TriggerPolicy;
        if (policy is not { Enabled: true })
        {
            RuntimeTelemetry.RecordAutomation(operation, "policy");
            throw new ArgumentException("Scheduling is disabled for this agent.");
        }

        if (requireUserScheduling && !policy.AllowUserScheduling)
        {
            RuntimeTelemetry.RecordAutomation(operation, "policy");
            throw new ArgumentException("Scheduling is disabled for this agent.");
        }

        return policy;
    }

    private static bool SchedulingEnabled(TriggerPolicy? policy) =>
        policy is { Enabled: true, AllowUserScheduling: true };

    private static long ResolveExpectedRevision(
        JsonElement arguments,
        TriggerCommandContext context,
        Automation current)
    {
        var id = RequireId(arguments);
        if (context.ScheduleContext is { AutomationId: var referentId } && referentId == id)
        {
            return current.Revision;
        }

        return RequireRevision(arguments);
    }

    private static string RequireTimeZone(JsonElement arguments, TriggerCommandContext context)
    {
        if (TryString(arguments, "timeZone", out var specified) && !string.IsNullOrWhiteSpace(specified))
        {
            return TriggerTimeZoneNormalization.Resolve(specified);
        }

        if (!string.IsNullOrWhiteSpace(context.ProfileTimeZoneId))
        {
            return TriggerTimeZoneNormalization.Resolve(context.ProfileTimeZoneId);
        }

        throw new ArgumentException(
            "Timezone is required. Ask which timezone to use, or pass a common label such as Vietnam time.");
    }

    private static string OptionalTimeZoneForDisplay(JsonElement arguments, TriggerCommandContext context)
    {
        if (TryString(arguments, "timeZone", out var specified) && !string.IsNullOrWhiteSpace(specified))
        {
            return TriggerTimeZoneNormalization.Resolve(specified);
        }

        if (!string.IsNullOrWhiteSpace(context.ProfileTimeZoneId))
        {
            return TriggerTimeZoneNormalization.Resolve(context.ProfileTimeZoneId);
        }

        return "UTC";
    }

    private static string RequireInstructions(JsonElement arguments) => TriggerText.RequireInstructions(RequireString(arguments, "instructions"));

    private static Guid RequireId(JsonElement arguments)
    {
        var text = RequireString(arguments, "automationId");
        return Guid.TryParse(text, out var id)
            ? id
            : throw new ArgumentException("automationId is invalid.");
    }

    private static long RequireRevision(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("expectedRevision", out var element) || !element.TryGetInt64(out var revision) || revision < 1)
        {
            throw new ArgumentException("expectedRevision is required.");
        }

        return revision;
    }

    private static TimeOnly RequireLocalTime(JsonElement arguments)
    {
        var text = RequireString(arguments, "localTime");
        return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw new ArgumentException("localTime must be a clock time such as 09:00.");
    }

    private static DateOnly? OptionalDate(JsonElement arguments, string name) =>
        TryString(arguments, name, out var text) && !string.IsNullOrWhiteSpace(text)
            ? ParseDate(text, name)
            : null;

    private static DateOnly ParseDate(string text, string name) =>
        DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{name} must be a calendar date.");

    private static DateTimeOffset ParseUtc(string text, string name)
    {
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant)
            || instant.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be a UTC timestamp.");
        }

        return instant;
    }

    private static IReadOnlyList<DayOfWeek> ReadWeekdays(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("weekdays", out var element) || element.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Weekly schedules require weekdays.");
        }

        var days = new List<DayOfWeek>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !Enum.TryParse<DayOfWeek>(item.GetString(), ignoreCase: true, out var day))
            {
                throw new ArgumentException("Weekly weekday is not valid.");
            }

            days.Add(day);
        }

        return days;
    }

    private static bool HasScheduleFields(JsonElement arguments) =>
        HasOneShotFields(arguments)
        || HasFixedIntervalScheduleFields(arguments)
        || HasCalendarRecurringFields(arguments);

    private static bool HasFixedIntervalScheduleFields(JsonElement arguments) =>
        arguments.TryGetProperty("intervalSeconds", out _)
        || arguments.TryGetProperty("endAtUtc", out _)
        || IsFixedIntervalKind(arguments);

    private static bool HasCalendarRecurringFields(JsonElement arguments) =>
        arguments.TryGetProperty("kind", out _)
        || arguments.TryGetProperty("localTime", out _)
        || arguments.TryGetProperty("weekdays", out _)
        || arguments.TryGetProperty("interval", out _)
        || arguments.TryGetProperty("startDate", out _)
        || arguments.TryGetProperty("endDate", out _)
        || arguments.TryGetProperty("maxOccurrences", out _);

    private static bool IsFixedIntervalKind(JsonElement arguments) =>
        TryString(arguments, "kind", out var kind)
        && (kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase));

    private static bool IsExplicitCalendarKindChange(JsonElement arguments) =>
        TryString(arguments, "kind", out var kind)
        && !kind.Equals("fixed_interval", StringComparison.OrdinalIgnoreCase)
        && !kind.Equals("fixedInterval", StringComparison.OrdinalIgnoreCase);

    private static (TriggerSchedule Schedule, DateTimeOffset? Next) ApplyFixedIntervalUpdate(
        FixedIntervalSchedule current,
        JsonElement arguments,
        TriggerCommandContext context,
        TriggerPolicy policy)
    {
        var intervalSeconds = current.IntervalSeconds;
        if (arguments.TryGetProperty("intervalSeconds", out var secondsElement)
            && secondsElement.ValueKind == JsonValueKind.Number
            && secondsElement.TryGetInt32(out var parsedSeconds))
        {
            intervalSeconds = parsedSeconds;
        }
        else
        {
            var fromText = ScheduleIntervalLanguage.TryParseIntervalSeconds(context.CurrentUserText);
            if (fromText is int textSeconds)
            {
                intervalSeconds = textSeconds;
            }
        }

        ScheduleDefinitionPolicy.RequireInterval(policy, intervalSeconds, context.UtcNow);

        var endAt = ResolveOptionalEndAtUtc(arguments, current.EndAtUtc);

        int? maxOccurrences = current.MaxOccurrences;
        if (arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null)
        {
            maxOccurrences = capElement.GetInt32();
        }

        var updated = new FixedIntervalSchedule(intervalSeconds, current.AnchorAtUtc, endAt, maxOccurrences);
        ScheduleDefinitionPolicy.Validate(updated, policy, context.UtcNow);
        var next = TriggerScheduleCalculator.InitialNext(updated, context.UtcNow)
            ?? throw new ArgumentException("No future occurrence matches this schedule.");
        return (updated, next);
    }

    private static bool HasOneShotFields(JsonElement arguments) =>
        TryPresent(arguments, "relativeDayOffset", out _)
        || TryTimeString(arguments, "localDate", out _)
        || TryTimeString(arguments, "atUtc", out _)
        || TryPresent(arguments, "relativeDelaySeconds", out _);

    private static bool TryTimeString(JsonElement arguments, string name, out string value)
    {
        if (!TryString(arguments, name, out value) || string.IsNullOrWhiteSpace(value))
        {
            value = "";
            return false;
        }

        value = value.Trim();
        return true;
    }

    private static bool TryWholeNumber(JsonElement element, out int number)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out number))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return true;
        }

        number = 0;
        return false;
    }

    private static bool TryPresent(JsonElement arguments, string name, out JsonElement element)
    {
        if (!arguments.TryGetProperty(name, out element)
            || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            element = default;
            return false;
        }

        return true;
    }

    private static string RequireString(JsonElement arguments, string name) =>
        TryString(arguments, name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required.");

    private static DateTimeOffset? ResolveOptionalEndAtUtc(JsonElement arguments, DateTimeOffset? current = null)
    {
        if (!arguments.TryGetProperty("endAtUtc", out var element))
        {
            return current;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            throw new TriggerScheduleCommandException(
                "schedule_validation_failed",
                "endAtUtc must be a valid ISO-8601 timestamp (normalized to UTC).");
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text) || !DateTimeOffset.TryParse(text, out var parsed))
        {
            throw new TriggerScheduleCommandException(
                "schedule_validation_failed",
                "endAtUtc must be a valid ISO-8601 timestamp (normalized to UTC).");
        }

        return TriggerScheduleCalculator.Truncate(parsed.ToUniversalTime());
    }

    private static bool TryString(JsonElement arguments, string name, out string value)
    {
        value = "";
        if (!arguments.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? "";
        return true;
    }

    private static bool? OptionalRequirement(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw AgentCoreErrors.Validation(name + " must be a boolean.");
        return value.GetBoolean();
    }

    private static AutomationExecutionTarget ChatTarget(JsonElement args, TriggerCommandContext context, Automation? current = null)
    {
        if (args.EnumerateObject().Any(p => p.Name is "sessionId" or "targetSessionId" or "reportToSessionId" or "agentInstanceId" or "profileId"))
            throw AgentCoreErrors.Validation("Core binds the destination; recipient identifiers are not permitted.");
        if (!args.TryGetProperty("executionTarget", out var mode)) return current?.ExecutionTarget ?? throw AgentCoreErrors.Validation("An explicit executionTarget is required.");
        return mode.GetString() switch { "currentSession" => AutomationExecutionTarget.Existing(context.SessionId),
            "backgroundSession" => AutomationExecutionTarget.Background, _ => throw AgentCoreErrors.Validation("executionTarget must be currentSession or backgroundSession.") };
    }
    private static AutomationCompletionDelivery ChatDelivery(JsonElement args, TriggerCommandContext context, Automation? current = null)
    {
        var target = ChatTarget(args, context, current);
        if (args.TryGetProperty("reportBack", out var report))
        {
            if (report.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || target.Kind != AutomationExecutionTargetKind.BackgroundSession)
                throw AgentCoreErrors.Validation("reportBack is only valid for backgroundSession.");
            return report.GetBoolean() ? AutomationCompletionDelivery.ToSession(context.SessionId) : AutomationCompletionDelivery.None;
        }
        return target.Kind == AutomationExecutionTargetKind.ExistingSession ? AutomationCompletionDelivery.None : current?.CompletionDelivery ?? AutomationCompletionDelivery.None;
    }

    internal static string RegistrationJson(Automation registration) =>
        JsonSerializer.Serialize(Projection(registration));

    private static object Projection(Automation registration) => new
    {
        automationId = registration.AutomationId,
        revision = registration.Revision,
        triggerRevision = registration.TriggerRevision,
        status = registration.Status.ToString(),
        name = registration.Name,
        instructions = registration.Instructions,
        executionTarget = new { kind = registration.ExecutionTarget.Kind == AutomationExecutionTargetKind.ExistingSession ? "existingSession" : "backgroundSession", sessionId = registration.ExecutionTarget.SessionId },
        completionDelivery = new { kind = registration.CompletionDelivery.SessionId is null ? "none" : "toSession", sessionId = registration.CompletionDelivery.SessionId },
        modelKey = registration.ModelOverrideCatalogKey,
        reasoningEffort = registration.ModelOverrideReasoningEffort,
        provenance = new { authorizationOrigin = registration.Provenance.AuthorizationOrigin.ToString(),
            sourceSessionId = registration.Provenance.SourceSessionId, sourceEventId = registration.Provenance.SourceEventId,
            createdAt = registration.Provenance.CreatedAt, updatedAt = registration.Provenance.UpdatedAt },
        trigger = registration.Trigger is EventTrigger eventTrigger
            ? (object)new { kind = "event", eventId = eventTrigger.EventId }
            : new { kind = "schedule", schedule = InspectSchedule(((ScheduleTrigger)registration.Trigger).Schedule) },
        triggerKind = registration.Trigger.Kind.ToString(),
        eventId = registration.EventId,
        scheduleKind = registration.Trigger is ScheduleTrigger scheduled ? scheduled.Schedule.Kind.ToString() : null,
        timeZone = registration.Trigger is ScheduleTrigger timing ? TimeZoneOf(timing.Schedule) : null,
        nextOccurrenceAtUtc = registration.NextOccurrenceAtUtc,
        occurrenceCount = registration.OccurrenceCount,
        suspensionReason = registration.SuspensionReason
    };

    private static object InspectSchedule(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule t => new { kind = "oneShot", timeZone = t.TimeZoneId, atUtc = t.AtUtc },
        FixedIntervalSchedule t => new { kind = "fixedInterval", interval = t.IntervalSeconds, anchorAtUtc = t.AnchorAtUtc, endAtUtc = t.EndAtUtc, maxOccurrences = t.MaxOccurrences },
        DailySchedule t => new { kind = "daily", timeZone = t.TimeZoneId, interval = t.IntervalDays, localTime = t.LocalTime.ToString("HH:mm"), startDate = t.StartDate, endDate = t.EndDate, maxOccurrences = t.MaxOccurrences },
        WeeklySchedule t => new { kind = "weekly", timeZone = t.TimeZoneId, interval = t.IntervalWeeks, localTime = t.LocalTime.ToString("HH:mm"), weekdays = t.Weekdays.Select(d => (int)d).ToArray(), startDate = t.StartDate, endDate = t.EndDate, maxOccurrences = t.MaxOccurrences },
        _ => throw AgentCoreErrors.Validation("Schedule timing is invalid.")
    };

    private static string TimeZoneOf(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => oneShot.TimeZoneId,
        DailySchedule daily => daily.TimeZoneId,
        WeeklySchedule weekly => weekly.TimeZoneId,
        FixedIntervalSchedule => "UTC",
        _ => ""
    };

    private static string Operation(string toolName) => toolName switch
    {
        ToolCatalog.AutomationUpdate => "update",
        ToolCatalog.AutomationDelete => "cancel",
        ToolCatalog.AutomationList => "list",
        _ => "create"
    };

    private static async ValueTask<TriggerCommandAuthorizationDecision> ResolveAuthorizationAsync(
        TriggerCommandContext context,
        ITriggerCommandAuthorizer authorizer,
        TriggerCommandAction required,
        CancellationToken cancellationToken)
    {
        if (context.ExecutePendingProposal)
        {
            if (context.PendingProposal is null)
            {
                return TriggerCommandAuthorizationDecision.Deny;
            }

            return TriggerAuthorization.ActionForTool(context.PendingProposal.ToolName) == required
                ? TriggerCommandAuthorizationDecision.Allow
                : TriggerCommandAuthorizationDecision.Deny;
        }

        if (context.Classification == TriggerAuthorizationClassification.CurrentUserTurn
            || context.Classification == TriggerAuthorizationClassification.UnrelatedUserTurn)
        {
            return await authorizer.AuthorizeCurrentTurnAsync(
                context.CurrentUserText,
                context.ConversationLanguage,
                required,
                context.ScheduleContext,
                context.ScheduleDraft,
                cancellationToken).ConfigureAwait(false);
        }

        return TriggerCommandAuthorizationDecision.Deny;
    }

    private static ToolExecutionResult Confirmation(string toolName, JsonElement arguments)
    {
        var proposal = new PendingTriggerProposal(toolName, arguments.GetRawText());
        var json = JsonSerializer.Serialize(new
        {
            error = "confirmation_required",
            message = "Ask the user to confirm this exact schedule proposal. Nothing was saved until they approve.",
            tool = toolName
        });
        return new ToolExecutionResult(json, ReplaceTriggerProposal: true, TriggerProposal: proposal);
    }

    private static ToolExecutionResult Result(
        string code,
        string message,
        bool clearProposal,
        ScheduleDraftContext? draft = null) =>
        new(Error(code, message, draft), ReplaceTriggerProposal: clearProposal, TriggerProposal: null);

    private static string Error(string code, string message, ScheduleDraftContext? draft = null)
    {
        if (draft is null)
        {
            return JsonSerializer.Serialize(new { error = code, message });
        }

        return JsonSerializer.Serialize(new
        {
            error = code,
            message,
            scheduleDraft = draft.ToJsonObject(),
            retryGuidance = code switch
            {
                "recurrence_below_minimum" => "The user may correct only the interval on the next turn. Do not change the intent.",
                "unsupported_recurrence" => "Explain supported cadences and ask the user to restate the request.",
                "authorization_denied" => "Do not retry with different time argument shapes.",
                _ => "Ask the user to restate the schedule request clearly."
            }
        });
    }
}
