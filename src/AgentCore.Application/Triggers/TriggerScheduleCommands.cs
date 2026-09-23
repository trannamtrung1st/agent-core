using System.Globalization;
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
    DateTimeOffset UtcNow);

public static class TriggerAuthorization
{
    public static TriggerAuthorizationResult Classify(
        TriggerKind kind,
        string? currentUserText,
        PendingTriggerProposal? pendingProposal,
        ITriggerCommandAuthorizer authorizer,
        string? conversationLanguage)
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

        var requested = authorizer.AuthorizeCurrentTurn(currentUserText, conversationLanguage);
        if (requested != TriggerCommandAction.None)
        {
            return new(TriggerAuthorizationClassification.CurrentUserTurn, requested);
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
        authorizer.AuthorizeCurrentTurn(text, conversationLanguage) != TriggerCommandAction.None;

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
        ITriggerCommandAuthorizer? authorizer = null)
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
        var allowed = ResolveAllowedActions(context, authorizer);
        if (required == TriggerCommandAction.None || (allowed & required) == 0)
        {
            if (context.Classification is TriggerAuthorizationClassification.Initiative or TriggerAuthorizationClassification.Environment
                && !context.ExecutePendingProposal
                && SchedulingEnabled(definition.TriggerPolicy))
            {
                RuntimeTelemetry.RecordTriggerRegistration(operation, "confirmation_required");
                return Confirmation(toolName, arguments);
            }

            if (context.Classification is TriggerAuthorizationClassification.CurrentUserTurn
                or TriggerAuthorizationClassification.UnrelatedUserTurn)
            {
                RuntimeTelemetry.RecordTriggerRegistration(operation, "current_turn_not_authorized");
                return Result(
                    "current_turn_not_authorized",
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

        try
        {
            var json = effectiveName switch
            {
                ToolCatalog.TriggerScheduleOnce => await CreateOnceAsync(
                    definition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerScheduleRecurring => await CreateRecurringAsync(
                    definition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerList => await ListAsync(
                    definition, registrations, owner, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerUpdate => await UpdateAsync(
                    definition, registrations, owner, context, effectiveArguments, cancellationToken).ConfigureAwait(false),
                ToolCatalog.TriggerCancel => await CancelAsync(
                    definition, registrations, owner, effectiveArguments, cancellationToken).ConfigureAwait(false),
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
        catch (ArgumentException exception)
        {
            return Result("validation", exception.Message, clearProposal: false);
        }
        catch (TriggerTimeZoneUnavailableException)
        {
            return Result("validation", "Timezone is unavailable. Ask the user for a different timezone.", clearProposal: false);
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
        var policy = RequirePolicy(definition, "create");
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
        var policy = RequirePolicy(definition, "create");
        await RequireCapacityAsync(registrations, owner, policy, cancellationToken).ConfigureAwait(false);
        var intent = RequireIntent(arguments);
        var schedule = ResolveRecurring(arguments, context, policy);
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
        RequirePolicy(definition, "list");
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
        var policy = RequirePolicy(definition, "update");
        var id = RequireId(arguments);
        var expected = RequireRevision(arguments);
        var current = await registrations.GetAsync(owner, id, cancellationToken).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("Trigger registration was not found.");
        var hasIntent = arguments.TryGetProperty("intent", out _);
        var hasSchedule = HasScheduleFields(arguments);
        TriggerRegistrationChange change;
        if (!hasIntent && !hasSchedule)
        {
            throw new ArgumentException("Update requires an intent or a schedule change.");
        }

        if (hasSchedule)
        {
            var (schedule, next) = current.Schedule is OneShotSchedule || HasOneShotFields(arguments)
                ? ResolveOneShot(arguments, context, policy)
                : (ResolveRecurring(arguments, context, policy), (DateTimeOffset?)null);
            if (schedule is not OneShotSchedule)
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
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        RequirePolicy(definition, "cancel");
        var cancelled = await registrations.CancelAsync(
            owner,
            RequireId(arguments),
            RequireRevision(arguments),
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

        var zone = TimeZone(arguments, context);
        var now = TriggerScheduleCalculator.Truncate(context.UtcNow);
        var hasOffset = arguments.TryGetProperty("relativeDayOffset", out var offsetElement);
        var hasDate = TryString(arguments, "localDate", out var localDateText);
        var hasAt = TryString(arguments, "atUtc", out var atText);
        var forms = (hasOffset ? 1 : 0) + (hasDate ? 1 : 0) + (hasAt ? 1 : 0);
        if (forms != 1)
        {
            throw new ArgumentException("Schedule time is missing or ambiguous. Ask the user for one exact time.");
        }

        DateTimeOffset instant;
        DateOnly? localDate = null;
        TimeOnly? localTime = null;
        if (hasOffset)
        {
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
            localDate = ParseDate(localDateText, "localDate");
            localTime = RequireLocalTime(arguments);
            instant = TriggerScheduleCalculator.ResolveWallClock(zone, localDate.Value, localTime.Value);
        }
        else
        {
            instant = ParseUtc(atText, "atUtc");
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
        var kind = RequireString(arguments, "kind");
        var zone = TimeZone(arguments, context);
        var localTime = RequireLocalTime(arguments);
        var interval = arguments.TryGetProperty("interval", out var intervalElement) && intervalElement.TryGetInt32(out var parsed)
            ? parsed
            : 1;
        var start = OptionalDate(arguments, "startDate");
        var end = OptionalDate(arguments, "endDate");
        int? cap = arguments.TryGetProperty("maxOccurrences", out var capElement) && capElement.ValueKind != JsonValueKind.Null
            ? capElement.GetInt32()
            : null;
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
                throw new ArgumentException("Daily interval is shorter than the policy minimum.");
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

        throw new ArgumentException("Schedule kind must be daily or weekly.");
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

    private static TriggerPolicy RequirePolicy(AgentDefinition definition, string operation)
    {
        var policy = definition.TriggerPolicy;
        if (!SchedulingEnabled(policy))
        {
            RuntimeTelemetry.RecordTriggerRegistration(operation, "policy");
            throw new ArgumentException("Scheduling is disabled for this agent.");
        }

        return policy!;
    }

    private static bool SchedulingEnabled(TriggerPolicy? policy) =>
        policy is { Enabled: true, AllowUserScheduling: true };

    private static string TimeZone(JsonElement arguments, TriggerCommandContext context)
    {
        if (TryString(arguments, "timeZone", out var specified) && !string.IsNullOrWhiteSpace(specified))
        {
            return TriggerTimeZone.Require(specified);
        }

        if (!string.IsNullOrWhiteSpace(context.ProfileTimeZoneId))
        {
            return TriggerTimeZone.Require(context.ProfileTimeZoneId);
        }

        throw new ArgumentException("Timezone is required. Ask the user which timezone to use.");
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
        || arguments.TryGetProperty("kind", out _)
        || arguments.TryGetProperty("localTime", out _)
        || arguments.TryGetProperty("weekdays", out _);

    private static bool HasOneShotFields(JsonElement arguments) =>
        arguments.TryGetProperty("relativeDayOffset", out _)
        || arguments.TryGetProperty("localDate", out _)
        || arguments.TryGetProperty("atUtc", out _);

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
        occurrenceCount = registration.OccurrenceCount
    };

    private static string TimeZoneOf(TriggerSchedule schedule) => schedule switch
    {
        OneShotSchedule oneShot => oneShot.TimeZoneId,
        DailySchedule daily => daily.TimeZoneId,
        WeeklySchedule weekly => weekly.TimeZoneId,
        _ => ""
    };

    private static string Operation(string toolName) => toolName switch
    {
        ToolCatalog.TriggerUpdate => "update",
        ToolCatalog.TriggerCancel => "cancel",
        ToolCatalog.TriggerList => "list",
        _ => "create"
    };

    private static TriggerCommandAction ResolveAllowedActions(
        TriggerCommandContext context,
        ITriggerCommandAuthorizer authorizer)
    {
        if (context.ExecutePendingProposal)
        {
            return context.PendingProposal is null
                ? TriggerCommandAction.None
                : TriggerAuthorization.ActionForTool(context.PendingProposal.ToolName);
        }

        if (context.Classification == TriggerAuthorizationClassification.CurrentUserTurn)
        {
            return authorizer.AuthorizeCurrentTurn(context.CurrentUserText, context.ConversationLanguage);
        }

        return context.AllowedActions;
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

    private static ToolExecutionResult Result(string code, string message, bool clearProposal) =>
        new(Error(code, message), ReplaceTriggerProposal: clearProposal, TriggerProposal: null);

    private static string Error(string code, string message) =>
        JsonSerializer.Serialize(new { error = code, message });
}
