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
        ToolCatalog.TriggerList => TriggerCommandAction.List,
        ToolCatalog.TriggerUpdate => TriggerCommandAction.Update,
        ToolCatalog.TriggerCancel => TriggerCommandAction.Cancel,
        ToolCatalog.TriggerScheduleOnce or ToolCatalog.TriggerScheduleRecurring => TriggerCommandAction.Create,
        _ => TriggerCommandAction.None
    };

}

public static class TriggerScheduleCommands
{
    private static readonly ITriggerCommandAuthorizer DefaultAuthorizer = new HeuristicTriggerCommandAuthorizer();

    public static async Task<ToolExecutionResult> ExecuteAsync(
        AgentDefinition definition,
        ITriggerRegistrationService? registrations,
        string toolName,
        JsonElement arguments,
        TriggerCommandContext? command,
        CancellationToken cancellationToken,
        ITriggerCommandAuthorizer? authorizer = null,
        IAgentInstanceStore? instances = null,
        IAgentDefinitionStore? definitions = null,
        IMemoryStore? profiles = null)
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
                RuntimeTelemetry.RecordTriggerRegistration(operation, "confirmation_required");
                return Confirmation(toolName, arguments);
            }

            if (authorization == TriggerCommandAuthorizationDecision.Ambiguous
                && context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                    or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordTriggerRegistration(operation, "authorization_ambiguous");
                return Result(
                    "authorization_ambiguous",
                    "The current message does not clearly authorize this schedule action. Ask what the user wants; do not retry different time argument shapes.",
                    clearProposal: false);
            }

            if (authorization == TriggerCommandAuthorizationDecision.ClassifierUnavailable
                && context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                    or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordTriggerRegistration(operation, "authorization_classifier_unavailable");
                return Result(
                    "authorization_classifier_unavailable",
                    "Schedule authorization is temporarily unavailable. Ask the user to restate the request or try again shortly.",
                    clearProposal: false);
            }

            if (context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordTriggerRegistration(operation, "authorization_denied");
                return Result(
                    "authorization_denied",
                    "The current user message does not authorize this schedule action. Clarify the request or use the correct schedule command.",
                    clearProposal: false);
            }

            RuntimeTelemetry.RecordTriggerRegistration(operation, "forbidden");
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
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!admission.Allowed)
                {
                    RuntimeTelemetry.RecordTriggerRegistration(operation, "policy");
                    return Result(
                        "policy",
                        TriggerDurableSchedulingPolicy.PolicyMessage(admission.DenialReason!.Value),
                        clearProposal: false);
                }

                policyDefinition = admission.Definition!;
            }
        }
        else if (required is TriggerCommandAction.Create or TriggerCommandAction.Update
                 && !TriggerDurableSchedulingPolicy.AllowsUserScheduling(definition))
        {
            RuntimeTelemetry.RecordTriggerRegistration(operation, "policy");
            return Result(
                "policy",
                "Scheduling is disabled for this agent.",
                clearProposal: false);
        }

        try
        {
            var json = effectiveName switch
            {
                ToolCatalog.TriggerScheduleOnce => await CreateOnceAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerScheduleRecurring => await CreateRecurringAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerList => await ListAsync(
                    policyDefinition, registrations, owner, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerUpdate => await UpdateAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerCancel => await CancelAsync(
                    policyDefinition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
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

    private static async Task<string> CreateOnceAsync(
        AgentDefinition definition,
        ITriggerRegistrationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var policy = RequirePolicy(definition, "create", requireUserScheduling: true);
        await RequireCapacityAsync(registrations, owner, policy, cancellationToken).ConfigureAwait(false);
        var intent = RequireIntent(arguments);
        var (schedule, next) = ResolveOneShot(arguments, context, policy);
        var created = await registrations.CreateAsync(
            new TriggerRegistrationDraft(
                owner,
                intent,
                schedule,
                next,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                context.SessionId,
                context.SourceEventId),
            cancellationToken).ConfigureAwait(false);
        return RegistrationJson(created);
    }

    private static async Task<string> CreateRecurringAsync(
        AgentDefinition definition,
        ITriggerRegistrationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var policy = RequirePolicy(definition, "create", requireUserScheduling: true);
        await RequireCapacityAsync(registrations, owner, policy, cancellationToken).ConfigureAwait(false);
        var merged = MergeDraftArguments(arguments, context);
        var intent = RequireIntent(merged);
        var schedule = ResolveRecurring(merged, context, policy);
        var next = TriggerScheduleCalculator.InitialNext(schedule, context.UtcNow)
            ?? throw new ArgumentException("No future occurrence matches this schedule.");
        var created = await registrations.CreateAsync(
            new TriggerRegistrationDraft(
                owner,
                intent,
                schedule,
                next,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                context.SessionId,
                context.SourceEventId),
            cancellationToken).ConfigureAwait(false);
        return RegistrationJson(created);
    }

    private static async Task<string> ListAsync(
        AgentDefinition definition,
        ITriggerRegistrationService registrations,
        TriggerOwner owner,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        RequirePolicy(definition, "list", requireUserScheduling: false);
        TriggerRegistrationStatus? status = null;
        if (TryString(arguments, "status", out var statusText) && !string.IsNullOrWhiteSpace(statusText))
        {
            if (!Enum.TryParse<TriggerRegistrationStatus>(statusText, ignoreCase: true, out var parsed))
            {
                throw new ArgumentException("Schedule status is invalid.");
            }

            status = parsed;
        }

        var rows = await registrations.ListAsync(owner, status, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            registrations = rows.Select(Projection).ToArray()
        });
    }

    private static async Task<string> UpdateAsync(
        AgentDefinition definition,
        ITriggerRegistrationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var policy = RequirePolicy(definition, "update", requireUserScheduling: true);
        var id = RequireId(arguments);
        var current = await registrations.GetAsync(owner, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        var expected = ResolveExpectedRevision(arguments, context, current);
        var hasIntent = arguments.TryGetProperty("intent", out _);
        var hasSchedule = HasScheduleFields(arguments);
        TriggerRegistrationChange change;
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

            change = TriggerRegistrationChange.ScheduleOnly(schedule, next, null);
            if (hasIntent)
            {
                change = TriggerRegistrationChange.Full(RequireIntent(arguments), schedule, next, null);
            }
        }
        else
        {
            change = TriggerRegistrationChange.IntentOnly(RequireIntent(arguments));
        }

        var updated = await registrations.UpdateAsync(owner, id, expected, change, cancellationToken).ConfigureAwait(false);
        return RegistrationJson(updated);
    }

    private static async Task<string> CancelAsync(
        AgentDefinition definition,
        ITriggerRegistrationService registrations,
        TriggerOwner owner,
        TriggerCommandContext context,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        RequirePolicy(definition, "cancel", requireUserScheduling: false);
        var id = RequireId(arguments);
        var current = await registrations.GetAsync(owner, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
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
        if (!policy.AllowOneShot)
        {
            throw new ArgumentException("One-shot schedules are not enabled.");
        }

        var now = TriggerScheduleCalculator.Truncate(context.UtcNow);
        var hasOffset = arguments.TryGetProperty("relativeDayOffset", out var offsetElement);
        var hasDate = TryString(arguments, "localDate", out var localDateText);
        var hasAt = TryString(arguments, "atUtc", out var atText);
        var hasDelay = arguments.TryGetProperty("relativeDelaySeconds", out var delayElement);
        var forms = (hasOffset ? 1 : 0) + (hasDate ? 1 : 0) + (hasAt ? 1 : 0) + (hasDelay ? 1 : 0);
        if (forms != 1)
        {
            throw new ArgumentException(
                "Schedule time is missing or ambiguous. Use exactly one of relativeDelaySeconds, relativeDayOffset with localTime, localDate with localTime, or atUtc. Ask the user to restate the full request with a clear time. Do not ask for a bare yes/no confirmation.");
        }

        DateTimeOffset instant;
        DateOnly? localDate = null;
        TimeOnly? localTime = null;
        string zone;
        if (hasOffset)
        {
            zone = RequireTimeZone(arguments, context);
            if (offsetElement.ValueKind != JsonValueKind.Number || !offsetElement.TryGetInt32(out var offset))
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
            if (delayElement.ValueKind != JsonValueKind.Number || !delayElement.TryGetInt32(out var seconds))
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
        if (instant <= now || instant > now.AddDays(policy.OneShotHorizonDays))
        {
            throw new ArgumentException("One-shot time must be in the future and inside the scheduling horizon.");
        }

        return (new OneShotSchedule(instant, zone, localDate, localTime), instant);
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
            if (!policy.AllowFixedInterval)
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Fixed-interval recurrence is not enabled for this agent.");
            }

            if (!arguments.TryGetProperty("intervalSeconds", out var secondsElement)
                || secondsElement.ValueKind != JsonValueKind.Number
                || !secondsElement.TryGetInt32(out var intervalSeconds))
            {
                throw new TriggerScheduleCommandException(
                    "schedule_validation_failed",
                    "Fixed-interval schedules require intervalSeconds.");
            }

            var intent = TryString(arguments, "intent", out var intentText) ? intentText : string.Empty;
            if (intervalSeconds < policy.MinFixedIntervalSeconds)
            {
                throw new TriggerScheduleCommandException(
                    "recurrence_below_minimum",
                    $"The minimum supported fixed interval is {policy.MinFixedIntervalSeconds} seconds. Ask the user for a longer interval.",
                    ScheduleDraftContext.ForFixedIntervalRejection(
                        intent,
                        intervalSeconds,
                        "recurrence_below_minimum",
                        context.UtcNow));
            }

            if (intervalSeconds > TriggerLimits.MaxFixedIntervalSeconds)
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Fixed interval is longer than the supported maximum.");
            }

            DateTimeOffset? endAt = null;
            if (TryString(arguments, "endAtUtc", out var endText) && DateTimeOffset.TryParse(endText, out var parsedEnd))
            {
                endAt = TriggerScheduleCalculator.Truncate(parsedEnd.ToUniversalTime());
            }

            if (cap is null && endAt is null && !policy.AllowIndefiniteRecurrence)
            {
                throw new TriggerScheduleCommandException(
                    "schedule_validation_failed",
                    "Indefinite recurrence is not enabled. Ask for an end date or occurrence cap.");
            }

            var anchor = TriggerScheduleCalculator.Truncate(context.UtcNow);
            return new FixedIntervalSchedule(intervalSeconds, anchor, endAt, cap);
        }

        var zone = RequireTimeZone(arguments, context);
        var localTime = RequireLocalTime(arguments);
        var interval = arguments.TryGetProperty("interval", out var intervalElement) && intervalElement.TryGetInt32(out var parsed)
            ? parsed
            : 1;
        var start = OptionalDate(arguments, "startDate");
        var end = OptionalDate(arguments, "endDate");
        if (cap is null && end is null && !policy.AllowIndefiniteRecurrence)
        {
            throw new ArgumentException("Indefinite recurrence is not enabled. Ask for an end date or occurrence cap.");
        }

        if (kind.Equals("daily", StringComparison.OrdinalIgnoreCase))
        {
            if (!policy.AllowDaily)
            {
                throw new ArgumentException("Daily schedules are not enabled.");
            }

            if (interval < policy.MinRecurrenceDays)
            {
                throw new TriggerScheduleCommandException(
                    "unsupported_recurrence",
                    "Daily schedules cannot represent sub-day recurrence. Use kind fixed_interval with intervalSeconds for minute or hour cadences.");
            }

            return new DailySchedule(interval, localTime, zone, start, end, cap);
        }

        if (kind.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            if (!policy.AllowWeekly)
            {
                throw new ArgumentException("Weekly schedules are not enabled.");
            }

            if (interval * 7 < policy.MinRecurrenceDays)
            {
                throw new ArgumentException("Weekly interval is shorter than the policy minimum.");
            }

            var weekdays = ReadWeekdays(arguments);
            return new WeeklySchedule(interval, weekdays, localTime, zone, start, end, cap);
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

            if (!root.TryGetProperty("intent", out var intentElement)
                || intentElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(intentElement.GetString()))
            {
                writer.WriteString("intent", draft.Intent);
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
        ITriggerRegistrationService registrations,
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
            RuntimeTelemetry.RecordTriggerRegistration(operation, "policy");
            throw new ArgumentException("Scheduling is disabled for this agent.");
        }

        if (requireUserScheduling && !policy.AllowUserScheduling)
        {
            RuntimeTelemetry.RecordTriggerRegistration(operation, "policy");
            throw new ArgumentException("Scheduling is disabled for this agent.");
        }

        return policy;
    }

    private static bool SchedulingEnabled(TriggerPolicy? policy) =>
        policy is { Enabled: true, AllowUserScheduling: true };

    private static long ResolveExpectedRevision(
        JsonElement arguments,
        TriggerCommandContext context,
        TriggerRegistration current)
    {
        var id = RequireId(arguments);
        if (context.ScheduleContext is { RegistrationId: var referentId } && referentId == id)
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

    private static string RequireIntent(JsonElement arguments) => TriggerText.RequireIntent(RequireString(arguments, "intent"));

    private static Guid RequireId(JsonElement arguments)
    {
        var text = RequireString(arguments, "registrationId");
        return Guid.TryParse(text, out var id)
            ? id
            : throw new ArgumentException("registrationId is invalid.");
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
        if (!policy.AllowFixedInterval)
        {
            throw new TriggerScheduleCommandException(
                "unsupported_recurrence",
                "Fixed-interval recurrence is not enabled for this agent.");
        }

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

        if (intervalSeconds < policy.MinFixedIntervalSeconds)
        {
            throw new TriggerScheduleCommandException(
                "recurrence_below_minimum",
                $"The minimum supported fixed interval is {policy.MinFixedIntervalSeconds} seconds.");
        }

        DateTimeOffset? endAt = current.EndAtUtc;
        if (TryString(arguments, "endAtUtc", out var endText)
            && DateTimeOffset.TryParse(endText, out var parsedEnd))
        {
            endAt = TriggerScheduleCalculator.Truncate(parsedEnd.ToUniversalTime());
        }

        int? maxOccurrences = current.MaxOccurrences;
        if (arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null)
        {
            maxOccurrences = capElement.GetInt32();
        }

        var updated = new FixedIntervalSchedule(intervalSeconds, current.AnchorAtUtc, endAt, maxOccurrences);
        var next = TriggerScheduleCalculator.InitialNext(updated, context.UtcNow)
            ?? throw new ArgumentException("No future occurrence matches this schedule.");
        return (updated, next);
    }

    private static bool HasOneShotFields(JsonElement arguments) =>
        arguments.TryGetProperty("relativeDayOffset", out _)
        || arguments.TryGetProperty("localDate", out _)
        || arguments.TryGetProperty("atUtc", out _)
        || arguments.TryGetProperty("relativeDelaySeconds", out _);

    private static string RequireString(JsonElement arguments, string name) =>
        TryString(arguments, name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new ArgumentException($"{name} is required.");

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

    private static string RegistrationJson(TriggerRegistration registration) =>
        JsonSerializer.Serialize(Projection(registration));

    private static object Projection(TriggerRegistration registration) => new
    {
        registrationId = registration.RegistrationId,
        revision = registration.Revision,
        scheduleRevision = registration.ScheduleRevision,
        status = registration.Status.ToString(),
        intent = registration.Intent,
        scheduleKind = registration.Schedule.Kind.ToString(),
        timeZone = TimeZoneOf(registration.Schedule),
        nextOccurrenceAtUtc = registration.NextOccurrenceAtUtc,
        occurrenceCount = registration.OccurrenceCount,
        suspensionReason = registration.SuspensionReason
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
        ToolCatalog.TriggerUpdate => "update",
        ToolCatalog.TriggerCancel => "cancel",
        ToolCatalog.TriggerList => "list",
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
