using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Events;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteExternalEventStore(IDbContextFactory<AgentCoreDbContext> contexts) : IExternalEventStore
{
    public async ValueTask<ExternalEventActivity> ReadActivityAsync(Guid resourceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var key = resourceId.ToString("D");
        var rows = await db.ExternalEvents.AsNoTracking().Where(e => e.ResourceId == key)
            .OrderByDescending(e => e.AdmittedAtUtc).Take(20).ToArrayAsync(ct);
        var ids = rows.Select(e => e.EventId).ToArray();
        var deliveries = await db.ExternalEventDeliveries.AsNoTracking().Where(d => ids.Contains(d.EventId)).ToArrayAsync(ct);
        return new(rows.Select(ToEvent).ToArray(), deliveries.Select(ToDelivery).ToArray());
    }

    public async ValueTask<WebhookEvent> CreateAsync(
        WebhookEvent source,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.WebhookEvents.Add(ToRecord(source));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            throw AgentCoreErrors.Conflict("Event key is already in use.");
        }

        return source;
    }

    public async ValueTask<WebhookEvent?> GetAsync(Guid resourceId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.WebhookEvents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.ResourceId == resourceId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSource(row);
    }

    public async ValueTask<WebhookEvent?> GetByKeyAsync(string eventKey, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var key = eventKey;
        var row = await db.WebhookEvents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EventKey == key, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToSource(row);
    }

    public async ValueTask<IReadOnlyList<WebhookEvent>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.WebhookEvents.AsNoTracking()
            .OrderBy(item => item.DisplayName)
            .ThenBy(item => item.ResourceId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToSource).ToArray();
    }

    public async ValueTask<WebhookEvent> SaveAsync(
        WebhookEvent source,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var id = source.ResourceId.ToString("D");
        var row = await db.WebhookEvents
            .FirstOrDefaultAsync(item => item.ResourceId == id, cancellationToken)
            .ConfigureAwait(false);
        if (row is null || row.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Event revision is stale.");
        }

        if (source.EventKey != row.EventKey || source.CreatedAtUtc.ToUnixTimeMilliseconds() != row.CreatedAtUtc || source.Revision != expectedRevision + 1)
            throw AgentCoreErrors.Validation("Event identity and key are immutable and revisions must advance by one.");
        Copy(row, source);
        try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateConcurrencyException) { throw AgentCoreErrors.Conflict("Event revision is stale."); }
        return source;
    }

    public async ValueTask<ExternalEventAdmit> AdmitAsync(
        ExternalEvent candidate,
        IReadOnlyList<ExternalEventTarget> targets,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var resourceId = candidate.ResourceId.ToString("D");
        var existing = await db.ExternalEvents.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ResourceId == resourceId && item.SourceEventId == candidate.SourceEventId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, ToEvent(existing));
        }

        db.ExternalEvents.Add(ToRecord(candidate));
        foreach (var target in targets)
        {
            db.ExternalEventDeliveries.Add(ToDelivery(candidate.EventId, target));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ExternalEventAdmit(ExternalEventAdmitKind.Admitted, candidate);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var raced = await db.ExternalEvents.AsNoTracking()
                .FirstAsync(
                    item => item.ResourceId == resourceId && item.SourceEventId == candidate.SourceEventId,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ExternalEventAdmit(ExternalEventAdmitKind.Duplicate, ToEvent(raced));
        }
    }

    public async ValueTask<IReadOnlyList<ExternalEventDelivery>> ListPendingDeliveriesAsync(
        Guid? eventId,
        int limit,
        CancellationToken cancellationToken = default, ExternalEventRecoveryCursor? after = null)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var query = db.ExternalEventDeliveries.AsNoTracking()
            .Where(item => item.Status == (int)ExternalEventDeliveryStatus.Pending
                || item.Status == (int)ExternalEventDeliveryStatus.FilterError && item.DecisionJson != null
                    && (EF.Functions.Like(item.DecisionJson, "%filter-worker-budget%") || EF.Functions.Like(item.DecisionJson, "%filter-timeout%")));
        if (eventId is Guid id)
        {
            var eventKey = id.ToString("D");
            query = query.Where(item => item.EventId == eventKey);
        }

        if (after is not null)
        {
            var eventKey = after.EventId.ToString("D"); var automationKey = after.AutomationId.ToString("D");
            query = query.Where(item => string.Compare(item.EventId, eventKey) > 0 || item.EventId == eventKey && string.Compare(item.AutomationId, automationKey) > 0);
        }
        var rows = await query
            .OrderBy(item => item.EventId)
            .ThenBy(item => item.AutomationId)
            .Take(Math.Clamp(limit, 1, 64))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(ToDelivery).ToArray();
    }

    public async ValueTask<EventFilterResult> DecideDeliveryAsync(Guid eventId, Guid automationId, EventFilterResult decision, CancellationToken ct = default, EventFilterResult? expectedDecision = null)
    { await using var db = await contexts.CreateDbContextAsync(ct);
        var prior = await db.ExternalEventDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D"), ct);
        var expectedJson = expectedDecision?.Retryable == true && prior?.DecisionJson is { } json
            && System.Text.Json.JsonSerializer.Deserialize<EventFilterResult>(json, CoreEventPersistence.Json) == expectedDecision ? json : null;
        await db.ExternalEventDeliveries.Where(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D") && d.DecisionJson == expectedJson && (d.Status == (int)ExternalEventDeliveryStatus.Pending || expectedJson != null && d.Status == (int)ExternalEventDeliveryStatus.FilterError))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, (int)ExternalEventDeliveryStatus.Pending)
                .SetProperty(d => d.DecisionJson, System.Text.Json.JsonSerializer.Serialize(decision, CoreEventPersistence.Json)), ct);
        var row = await db.ExternalEventDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D"), ct);
        return row?.DecisionJson is not null && row.Status != (int)ExternalEventDeliveryStatus.Skipped
            ? System.Text.Json.JsonSerializer.Deserialize<EventFilterResult>(row.DecisionJson, CoreEventPersistence.Json)!
            : new(null, "error", "delivery-unavailable");
    }

    public async ValueTask MarkDeliveryAsync(
        Guid eventId,
        Guid automationId,
        ExternalEventDeliveryStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var eventKey = eventId.ToString("D");
        var registrationKey = automationId.ToString("D");
        var row = await db.ExternalEventDeliveries
            .FirstOrDefaultAsync(
                item => item.EventId == eventKey && item.AutomationId == registrationKey,
                cancellationToken)
            .ConfigureAwait(false);
        if (row is null || (row.Status != (int)ExternalEventDeliveryStatus.Pending
            && !(row.Status == (int)ExternalEventDeliveryStatus.FilterError && row.DecisionJson is { } json
                && System.Text.Json.JsonSerializer.Deserialize<EventFilterResult>(json, CoreEventPersistence.Json)?.Retryable == true)))
        {
            return;
        }

        row.Status = (int)status;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ExternalEvent?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExternalEvents.AsNoTracking()
            .FirstOrDefaultAsync(item => item.EventId == eventId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToEvent(row);
    }

    public async ValueTask<ExternalEvent?> GetEventAsync(
        Guid resourceId,
        string sourceEventId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.ExternalEvents.AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.ResourceId == resourceId.ToString("D") && item.SourceEventId == sourceEventId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : ToEvent(row);
    }

    private static WebhookEventRecord ToRecord(WebhookEvent source) => new()
    {
        ResourceId = source.ResourceId.ToString("D"),
        DisplayName = source.DisplayName,
        Kind = (int)source.Kind,
        EventKey = source.EventKey,
        CredentialHash = source.CredentialHash,
        Status = (int)source.Status,
        Revision = source.Revision,
        CreatedAtUtc = source.CreatedAtUtc.ToUnixTimeMilliseconds(),
        UpdatedAtUtc = source.UpdatedAtUtc.ToUnixTimeMilliseconds()
    };

    private static void Copy(WebhookEventRecord row, WebhookEvent source)
    {
        row.DisplayName = source.DisplayName;
        row.CredentialHash = source.CredentialHash;
        row.Status = (int)source.Status;
        row.Revision = source.Revision;
        row.UpdatedAtUtc = source.UpdatedAtUtc.ToUnixTimeMilliseconds();
    }

    private static WebhookEvent ToSource(WebhookEventRecord row) => new(
        Guid.Parse(row.ResourceId),
        row.DisplayName,
        (WebhookEventKind)row.Kind,
        row.EventKey,
        row.CredentialHash,
        (WebhookEventStatus)row.Status,
        row.Revision,
        DateTimeOffset.FromUnixTimeMilliseconds(row.CreatedAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.UpdatedAtUtc));

    private static ExternalEventRecord ToRecord(ExternalEvent item) => new()
    {
        EventId = item.EventId.ToString("D"),
        ResourceId = item.ResourceId.ToString("D"),
        SourceEventId = item.SourceEventId,
        OccurredAtUtc = item.OccurredAtUtc.ToUnixTimeMilliseconds(),
        AdmittedAtUtc = item.AdmittedAtUtc.ToUnixTimeMilliseconds(),
        EvidenceJson = item.EvidenceJson
    };

    private static ExternalEventDeliveryRecord ToDelivery(Guid eventId, ExternalEventTarget target) => new()
    {
        EventId = eventId.ToString("D"),
        AutomationId = target.AutomationId.ToString("D"),
        AgentInstanceId = target.AgentInstanceId.ToString("D"),
        ProfileId = target.ProfileId.ToString("D"),
        Status = (int)ExternalEventDeliveryStatus.Pending,
        SnapshotJson = target.Snapshot is null ? null : System.Text.Json.JsonSerializer.Serialize(target.Snapshot, CoreEventPersistence.Json)
    };

    private static ExternalEventDelivery ToDelivery(ExternalEventDeliveryRecord row) => new(
        Guid.Parse(row.EventId),
        Guid.Parse(row.AutomationId),
        Guid.Parse(row.AgentInstanceId),
        Guid.Parse(row.ProfileId),
        (ExternalEventDeliveryStatus)row.Status,
        row.SnapshotJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<EventSubscriptionSnapshot>(row.SnapshotJson, CoreEventPersistence.Json),
        row.DecisionJson is null ? null : System.Text.Json.JsonSerializer.Deserialize<EventFilterResult>(row.DecisionJson, CoreEventPersistence.Json));

    private static ExternalEvent ToEvent(ExternalEventRecord row) => new(
        Guid.Parse(row.EventId),
        Guid.Parse(row.ResourceId),
        row.SourceEventId,
        DateTimeOffset.FromUnixTimeMilliseconds(row.OccurredAtUtc),
        DateTimeOffset.FromUnixTimeMilliseconds(row.AdmittedAtUtc),
        row.EvidenceJson);
}
