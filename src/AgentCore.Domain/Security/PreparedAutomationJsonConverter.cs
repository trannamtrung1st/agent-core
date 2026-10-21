using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// Closed consent serialization only. Ordinary Automation/trigger persistence and
// public HTTP contracts keep their incumbent mappings and historical JSON.
public sealed class PreparedAutomationJsonConverter : JsonConverter<Automation>
{
    private sealed record Timing(TriggerScheduleKind Kind, string TimeZoneId, DateTimeOffset? AtUtc = null, int Interval = 1,
        TimeOnly? LocalTime = null, IReadOnlyList<DayOfWeek>? Weekdays = null, DateOnly? StartDate = null, DateOnly? EndDate = null,
        int? MaxOccurrences = null, DateTimeOffset? AnchorAtUtc = null, DateTimeOffset? EndAtUtc = null);
    private sealed record Saved(Guid AutomationId, TriggerOwner Owner, AutomationStatus Status, string Name, string Instructions,
        Guid TriggerId, bool TriggerEnabled, long ChildRevision, Timing Timing, DateTimeOffset? NextOccurrenceAtUtc, DateTimeOffset? ExpiresAtUtc,
        int OccurrenceCount, long Revision, long TriggerRevision, TriggerProvenance Provenance, string? SuspensionReason,
        string? ModelOverrideCatalogKey, string? ModelOverrideReasoningEffort, bool RequiresTools, bool RequiresVision);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SavedChild(Guid TriggerId, bool Enabled, long Revision, AutomationTriggerKind Kind,
        Timing? Timing = null, Guid? WebhookId = null, string? CoreEventKey = null, string? FilterExpression = null, EventDispatch? Dispatch = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record SavedEvents(int SchemaVersion, Guid AutomationId, TriggerOwner Owner, AutomationStatus Status, string Name, string Instructions,
        IReadOnlyList<SavedChild> Triggers, DateTimeOffset? NextOccurrenceAtUtc, DateTimeOffset? ExpiresAtUtc, int OccurrenceCount,
        long Revision, long TriggerRevision, TriggerProvenance Provenance, string? SuspensionReason,
        string? ModelOverrideCatalogKey, string? ModelOverrideReasoningEffort, bool RequiresTools, bool RequiresVision,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ReportToSessionId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TargetSessionId = null);

    private static TriggerSchedule Schedule(Timing t) => (t ?? throw new JsonException("Consent timing is missing.")).Kind switch {
        TriggerScheduleKind.OneShot when t.AtUtc is { } at => new OneShotSchedule(at, t.TimeZoneId),
        TriggerScheduleKind.Daily when t.LocalTime is { } local => new DailySchedule(t.Interval, local, t.TimeZoneId, t.StartDate, t.EndDate, t.MaxOccurrences),
        TriggerScheduleKind.Weekly when t.LocalTime is { } local && t.Weekdays is { } days => new WeeklySchedule(t.Interval, days, local, t.TimeZoneId, t.StartDate, t.EndDate, t.MaxOccurrences),
        TriggerScheduleKind.FixedInterval when t.AnchorAtUtc is { } anchor => new FixedIntervalSchedule(t.Interval, anchor, t.EndAtUtc, t.MaxOccurrences),
        _ => throw new JsonException("An exact supported consent schedule is required.") };
    private static Timing TimingOf(TriggerSchedule t) => t switch {
        OneShotSchedule s => new Timing(t.Kind, s.TimeZoneId, s.AtUtc),
        DailySchedule s => new Timing(t.Kind, s.TimeZoneId, Interval: s.IntervalDays, LocalTime: s.LocalTime, StartDate: s.StartDate, EndDate: s.EndDate, MaxOccurrences: s.MaxOccurrences),
        WeeklySchedule s => new Timing(t.Kind, s.TimeZoneId, Interval: s.IntervalWeeks, LocalTime: s.LocalTime, Weekdays: s.Weekdays, StartDate: s.StartDate, EndDate: s.EndDate, MaxOccurrences: s.MaxOccurrences),
        FixedIntervalSchedule s => new Timing(t.Kind, "UTC", Interval: s.IntervalSeconds, MaxOccurrences: s.MaxOccurrences, AnchorAtUtc: s.AnchorAtUtc, EndAtUtc: s.EndAtUtc),
        _ => throw new JsonException("Unsupported consent schedule.") };
    private static AutomationTriggerRecord Child(SavedChild t)
    {
        if (t is null) throw new JsonException("Consent trigger is missing.");
        AutomationTrigger configuration = t.Kind switch {
            AutomationTriggerKind.Schedule when t.Timing is not null && t.WebhookId is null && t.CoreEventKey is null && t.FilterExpression is null && t.Dispatch is null => new ScheduleTrigger(Schedule(t.Timing)),
            AutomationTriggerKind.Event when t.Timing is null && t.WebhookId is { } id && t.CoreEventKey is null && t.Dispatch is not null => new EventTrigger(id, t.FilterExpression, t.Dispatch),
            AutomationTriggerKind.CoreEvent when t.Timing is null && t.WebhookId is null && t.CoreEventKey is { } key && t.Dispatch is not null => new CoreEventTrigger(key, t.FilterExpression, t.Dispatch),
            _ => throw new JsonException("Consent trigger has conflicting or missing source fields.") };
        return new(t.TriggerId, configuration, t.Enabled, t.Revision);
    }

    public override Automation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("Automation consent must be an object.");
        var schemaName = options.PropertyNamingPolicy?.ConvertName(nameof(SavedEvents.SchemaVersion)) ?? nameof(SavedEvents.SchemaVersion);
        var versions = document.RootElement.EnumerateObject().Where(p => string.Equals(p.Name, schemaName,
            options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray();
        if (versions.Length > 1) throw new JsonException("Automation consent schema is duplicated.");
        if (versions.Length == 1)
        {
            var schema = versions[0].Value;
            if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version is not (2 or 3 or 4)) throw new JsonException("Unsupported Automation consent source schema.");
            RequireUnique(document.RootElement, options);
            var events = document.RootElement.Deserialize<SavedEvents>(options) ?? throw new JsonException("Automation consent source is missing.");
            if (events.Triggers is null) throw new JsonException("Consent triggers are missing.");
            var reportName = options.PropertyNamingPolicy?.ConvertName(nameof(SavedEvents.ReportToSessionId)) ?? nameof(SavedEvents.ReportToSessionId);
            var hasReport = document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, reportName,
                options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            var targetName = options.PropertyNamingPolicy?.ConvertName(nameof(SavedEvents.TargetSessionId)) ?? nameof(SavedEvents.TargetSessionId);
            var hasTarget = document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, targetName,
                options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            var validDestination = version switch {
                2 => !hasReport && !hasTarget,
                3 => hasReport && events.ReportToSessionId is { } report && report != Guid.Empty && !hasTarget,
                4 => hasTarget && events.TargetSessionId is { } target && target != Guid.Empty && !hasReport,
                _ => false };
            if (!validDestination) throw new JsonException("Consent destination does not match its exact source schema.");
            var ownerName = options.PropertyNamingPolicy?.ConvertName(nameof(SavedEvents.Owner)) ?? nameof(SavedEvents.Owner);
            var ownerJson = document.RootElement.EnumerateObject().Single(p => string.Equals(p.Name, ownerName,
                options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).Value;
            var ownerKeys = new[] { nameof(TriggerOwner.AgentInstanceId), nameof(TriggerOwner.TenantId), nameof(TriggerOwner.ExecutorPrincipalId) }
                .Select(n => options.PropertyNamingPolicy?.ConvertName(n) ?? n).Append("profileId")
                .ToHashSet(options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            if (ownerJson.ValueKind != JsonValueKind.Object || ownerJson.EnumerateObject().Any(p => !ownerKeys.Contains(p.Name)))
                throw new JsonException("Consent owner contains unknown fields.");
            var source = new Automation(events.AutomationId, events.Owner, events.Status, events.Instructions, events.Triggers.Select(Child).ToArray(),
                events.NextOccurrenceAtUtc, events.ExpiresAtUtc, events.OccurrenceCount, events.Revision, events.TriggerRevision, events.Provenance,
                events.SuspensionReason, events.ModelOverrideCatalogKey, events.ModelOverrideReasoningEffort, events.RequiresVision, events.Name,
                events.TargetSessionId is { } targetId ? AutomationExecutionTarget.Existing(targetId) : AutomationExecutionTarget.Background,
                new AutomationCompletionDelivery(events.ReportToSessionId), events.RequiresTools);
            if (source.TriggerRevision != events.TriggerRevision) throw new JsonException("Automation consent trigger revision differs.");
            return source;
        }
        var p = document.RootElement.Deserialize<Saved>(options) ?? throw new JsonException("Automation consent source is missing.");
        var schedule = Schedule(p.Timing);
        var value = new Automation(p.AutomationId, p.Owner, p.Status, p.Instructions,
            new[] { new AutomationTriggerRecord(p.TriggerId, new ScheduleTrigger(schedule), p.TriggerEnabled, p.ChildRevision) },
            p.NextOccurrenceAtUtc, p.ExpiresAtUtc, p.OccurrenceCount, p.Revision, p.TriggerRevision, p.Provenance, p.SuspensionReason,
            p.ModelOverrideCatalogKey, p.ModelOverrideReasoningEffort, p.RequiresVision, p.Name, AutomationExecutionTarget.Background,
            AutomationCompletionDelivery.None, p.RequiresTools);
        if (value.TriggerRevision != p.TriggerRevision) throw new JsonException("Automation consent trigger revision differs.");
        return value;
    }

    private static void RequireUnique(JsonElement element, JsonSerializerOptions options)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(options.PropertyNameCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Consent source contains duplicated fields.");
                RequireUnique(property.Value, options);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RequireUnique(child, options);
    }

    public override void Write(Utf8JsonWriter writer, Automation value, JsonSerializerOptions options)
    {
        var target = value.ExecutionTarget.SessionId;
        var report = value.CompletionDelivery.SessionId;
        if (target is not null && report is not null) throw new JsonException("Existing execution cannot have a separate report destination.");
        if (target is not null || report is not null || value.Triggers.Count != 1 || value.Triggers[0].Configuration is not ScheduleTrigger)
        {
            var children = value.Triggers.Select(t => t.Configuration switch {
                ScheduleTrigger schedule => new SavedChild(t.TriggerId, t.Enabled, t.Revision, t.Configuration.Kind, TimingOf(schedule.Schedule)),
                EventTrigger webhook => new SavedChild(t.TriggerId, t.Enabled, t.Revision, t.Configuration.Kind, WebhookId: webhook.EventId, FilterExpression: webhook.FilterExpression, Dispatch: webhook.Dispatch),
                CoreEventTrigger core => new SavedChild(t.TriggerId, t.Enabled, t.Revision, t.Configuration.Kind, CoreEventKey: core.CoreEventKey, FilterExpression: core.FilterExpression, Dispatch: core.Dispatch),
                _ => throw new JsonException("Unsupported consent trigger.") }).ToArray();
            JsonSerializer.Serialize(writer, new SavedEvents(target is not null ? 4 : report is not null ? 3 : 2, value.AutomationId, value.Owner, value.Status, value.Name, value.Instructions,
                children, value.NextOccurrenceAtUtc, value.ExpiresAtUtc, value.OccurrenceCount, value.Revision, value.TriggerRevision, value.Provenance,
                value.SuspensionReason, value.ModelOverrideCatalogKey, value.ModelOverrideReasoningEffort, value.RequiresTools, value.RequiresVision, report, target), options);
            return;
        }
        // Preserve already-prepared one-Schedule consent bytes and their hashes.
        var timing = TimingOf(((ScheduleTrigger)value.Triggers[0].Configuration).Schedule);
        var child = value.Triggers[0];
        JsonSerializer.Serialize(writer, new Saved(value.AutomationId, value.Owner, value.Status, value.Name, value.Instructions,
            child.TriggerId, child.Enabled, child.Revision, timing, value.NextOccurrenceAtUtc, value.ExpiresAtUtc, value.OccurrenceCount,
            value.Revision, value.TriggerRevision, value.Provenance, value.SuspensionReason, value.ModelOverrideCatalogKey,
            value.ModelOverrideReasoningEffort, value.RequiresTools, value.RequiresVision), options);
    }
}
