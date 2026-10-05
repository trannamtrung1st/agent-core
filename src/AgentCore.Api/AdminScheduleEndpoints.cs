using System.Globalization;
using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Experience;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Api;

internal static class AdminScheduleEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        var group = admin.MapGroup("/agent-instances/{instanceId:guid}/schedules");
        group.MapGet("", (Guid instanceId, ExperienceService instances, ITriggerStore store, IWorkItemStore work,
            IAgentDefinitionStore definitions, IModelCatalog catalog, CancellationToken ct) => Respond(async () =>
        {
            var instance = await instances.RequireInstanceAsync(instanceId, ct);
            var definition = await definitions.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct);
            var recent = await work.ListAsync(new(instanceId, LocalUserProfile.Id), 100, ct);
            var rows = await store.ListAsync(new(instanceId, LocalUserProfile.Id), null, ct);
            return new AdminScheduleReview(rows.Where(r => r.EventSourceId is null && r.Provenance.AuthorizationOrigin != TriggerAuthorizationOrigin.AdminThought)
                .Select(r => Project(r, definition is null ? null : ExecutionModelPolicy.Resolve(catalog, definition, instance, r).Pin?.CatalogKey,
                    recent.FirstOrDefault(w => w.Provenance.RegistrationId == r.RegistrationId))).ToArray());
        }));
        group.MapPost("", (Guid instanceId, AdminScheduleRequest request, AdminScheduleService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, null, request.ExpectedRevision, request.Enabled, request.Intent, Parse(request.Schedule), request.ModelKey, request.ReasoningEffort, ct))));
        group.MapPut("/{registrationId:guid}", (Guid instanceId, Guid registrationId, AdminScheduleRequest request, AdminScheduleService service, CancellationToken ct) => Respond(async () =>
            Project(await service.SaveAsync(instanceId, registrationId, request.ExpectedRevision, request.Enabled, request.Intent, Parse(request.Schedule), request.ModelKey, request.ReasoningEffort, ct))));
        group.MapPost("/{registrationId:guid}/cancel", (Guid instanceId, Guid registrationId, ContinuityRevisionRequest request, AdminScheduleService service, CancellationToken ct) => Respond(async () =>
        { await service.DeleteAsync(instanceId, registrationId, request.ExpectedRevision, ct); return new { cancelled = true }; }));
        group.MapPost("/{registrationId:guid}/run", (Guid instanceId, Guid registrationId, ContinuityRevisionRequest request, AdminScheduleService service, CancellationToken ct) => Respond(async () =>
        { var occurrence = await service.RunNowAsync(instanceId, registrationId, request.ExpectedRevision, ct); return new { occurrenceId = occurrence.OccurrenceId }; }));
    }
    private static TriggerSchedule Parse(AdminScheduleTiming? t)
    {
        if (t is null) throw AgentCoreErrors.Validation("Schedule timing is required.");
        DateTimeOffset Utc(string? value) => DateTimeOffset.Parse(value ?? "", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
        DateOnly? Date(string? value) => value is null ? null : DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        TimeOnly Time() => TimeOnly.ParseExact(t.LocalTime ?? "", "HH:mm", CultureInfo.InvariantCulture);
        return t.Kind switch
        {
            "oneShot" => new OneShotSchedule(Utc(t.AtUtc), t.TimeZone),
            "fixedInterval" => new FixedIntervalSchedule(t.Interval, Utc(t.AnchorAtUtc), t.EndAtUtc is null ? null : Utc(t.EndAtUtc), t.MaxOccurrences),
            "daily" => new DailySchedule(t.Interval, Time(), t.TimeZone, Date(t.StartDate), Date(t.EndDate), t.MaxOccurrences),
            "weekly" => new WeeklySchedule(t.Interval, (t.Weekdays ?? []).Select(d => (DayOfWeek)d).ToArray(), Time(), t.TimeZone, Date(t.StartDate), Date(t.EndDate), t.MaxOccurrences),
            _ => throw AgentCoreErrors.Validation("Schedule kind is invalid.")
        };
    }
    private static AdminScheduleResponse Project(TriggerRegistration r, string? effective = null, AgentCore.Domain.Work.WorkItem? work = null) =>
        new(r.RegistrationId.ToString("D"), r.Revision, r.Intent, r.Status == TriggerRegistrationStatus.Active, r.Status.ToString(), Timing(r.Schedule),
            r.Provenance.AuthorizationOrigin.ToString(), r.Provenance.SourceSessionId?.ToString("D"), r.Provenance.SourceEventId?.ToString("D"),
            HttpMapping.Format(r.Provenance.CreatedAt), r.NextOccurrenceAtUtc is { } next ? HttpMapping.Format(next) : null,
            r.ModelOverrideCatalogKey, r.ModelOverrideReasoningEffort, effective, work?.WorkItemId.ToString("D"), work?.Status.ToString());
    private static AdminScheduleTiming Timing(TriggerSchedule s) => s switch
    {
        OneShotSchedule t => new("oneShot", t.TimeZoneId, HttpMapping.Format(t.AtUtc)),
        FixedIntervalSchedule t => new("fixedInterval", Interval: t.IntervalSeconds, AnchorAtUtc: HttpMapping.Format(t.AnchorAtUtc), EndAtUtc: t.EndAtUtc is { } e ? HttpMapping.Format(e) : null, MaxOccurrences: t.MaxOccurrences),
        DailySchedule t => new("daily", t.TimeZoneId, Interval: t.IntervalDays, LocalTime: t.LocalTime.ToString("HH:mm"), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        WeeklySchedule t => new("weekly", t.TimeZoneId, Interval: t.IntervalWeeks, LocalTime: t.LocalTime.ToString("HH:mm"), Weekdays: t.Weekdays.Select(d => (int)d).ToArray(), StartDate: t.StartDate?.ToString("yyyy-MM-dd"), EndDate: t.EndDate?.ToString("yyyy-MM-dd"), MaxOccurrences: t.MaxOccurrences),
        _ => throw AgentCoreErrors.Validation("Schedule kind is unavailable.")
    };
    private static async Task<IResult> Respond<T>(Func<Task<T>> action)
    {
        try { return Results.Json(await action()); }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or TriggerTimeZoneUnavailableException)
        { return ProblemResults.From(AgentCoreErrors.Validation("Schedule timing or configuration is invalid.")); }
    }
}
