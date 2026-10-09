using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SqliteCoreEventStore(IDbContextFactory<AgentCoreDbContext> contexts) : ICoreEventStore
{
    public async ValueTask<EventCoveragePage> CoveragePageAsync(TriggerOwner owner, Guid automationId, string? cursor, int limit, CancellationToken ct = default)
    {
        var after = EventCoverage.Cursor(cursor);
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CoreEventBuckets.AsNoTracking().Where(b => b.Flushed && b.AutomationId == automationId.ToString("D") && string.Compare(b.BucketId, after.Bucket) >= 0).OrderBy(b => b.BucketId).Take(32).ToArrayAsync(ct);
        return EventCoverage.Page(rows.Select(r => (JsonSerializer.Deserialize<CoreEventBucket>(r.PayloadJson, CoreEventPersistence.Json)!, (IReadOnlyCollection<Guid>)(JsonSerializer.Deserialize<Guid[]>(r.CoverageJson) ?? []))), owner, cursor, limit, rows.Length == 32);
    }
    public async ValueTask MarkSourceInspectedAsync(TriggerOwner owner, Guid bucketId, Guid sourceId, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CoreEventBuckets.SingleOrDefaultAsync(b => b.BucketId == bucketId.ToString("D"), ct);
        if (row is null) return;
        var bucket = JsonSerializer.Deserialize<CoreEventBucket>(row.PayloadJson, CoreEventPersistence.Json)!;
        if (bucket.Subscription.Owner != owner) return;
        var rows = await db.CoreEventBuckets.Where(b => b.AutomationId == row.AutomationId && EF.Functions.Like(b.PayloadJson, "%" + sourceId.ToString("D") + "%")).ToArrayAsync(ct);
        foreach (var prior in rows)
        {
            bucket = JsonSerializer.Deserialize<CoreEventBucket>(prior.PayloadJson, CoreEventPersistence.Json)!;
            if (bucket.Subscription.Owner != owner) continue;
            var covered = (JsonSerializer.Deserialize<Guid[]>(prior.CoverageJson) ?? []).ToHashSet();
            foreach (var e in bucket.Sources.Where(e => EventCoverage.SourceRunId(e) == sourceId)) covered.Add(e.EventId);
            prior.CoverageJson = JsonSerializer.Serialize(covered.Order());
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async ValueTask CoalesceAsync(EventBucketSource source, EventSubscriptionSnapshot subscription, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var delivery = await db.CoreEventDeliveries.SingleOrDefaultAsync(d => d.EventId == source.EventId.ToString("D") && d.AutomationId == subscription.AutomationId.ToString("D"), ct);
        if (subscription.SourceKind == TriggerSourceKind.CoreEvent && delivery?.Status != (int)EventMatchStatus.Matched) return;
        if (await db.CoreEventBuckets.AnyAsync(b => b.AutomationId == subscription.AutomationId.ToString("D") && EF.Functions.Like(b.PayloadJson, "%" + source.EventId.ToString("D") + "%"), ct)) return;
        var rows = await db.CoreEventBuckets.Where(b => !b.Flushed && b.AutomationId == subscription.AutomationId.ToString("D") && b.TriggerRevision == subscription.TriggerRevision && b.DueAtUtc > source.ReceivedAtUtc.ToUnixTimeMilliseconds()).OrderBy(b => b.DueAtUtc).Take(32).ToArrayAsync(ct);
        var row = rows.FirstOrDefault(r => { var b = JsonSerializer.Deserialize<CoreEventBucket>(r.PayloadJson, CoreEventPersistence.Json)!; return b.Sources.Count < 24 && b.Sources.Sum(e => e.DataJson.Length + 180) + source.DataJson.Length + 180 < 5500; });
        CoreEventBucket bucket;
        if (row is null)
        {
            bucket = new(CoreEventPersistence.Id($"bucket:{source.EventId:D}:{subscription.AutomationId:D}"), subscription, source.ReceivedAtUtc.AddSeconds(subscription.Dispatch.WindowSeconds!.Value), [source]);
            if (await db.CoreEventBuckets.CountAsync(b => !b.Flushed && b.AutomationId == subscription.AutomationId.ToString("D"), ct) >= 32)
                bucket = bucket with { Flushed = true, CompletionCode = "bucket-capacity" };
            row = new() { BucketId = bucket.BucketId.ToString("D"), AutomationId = subscription.AutomationId.ToString("D"), TriggerRevision = subscription.TriggerRevision, DueAtUtc = bucket.DueAtUtc.ToUnixTimeMilliseconds(), Flushed = bucket.Flushed };
            db.CoreEventBuckets.Add(row);
        }
        else { bucket = JsonSerializer.Deserialize<CoreEventBucket>(row.PayloadJson, CoreEventPersistence.Json)!; bucket = bucket with { Sources = bucket.Sources.Append(source).ToArray() }; }
        row.PayloadJson = JsonSerializer.Serialize(bucket, CoreEventPersistence.Json);
        if (delivery is not null) { delivery.Status = (int)(bucket.CompletionCode is null ? EventMatchStatus.Coalesced : EventMatchStatus.BudgetSkipped); delivery.Code = bucket.CompletionCode; }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<CoreEventBucket>> DueBucketsAsync(DateTimeOffset now, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.CoreEventBuckets.AsNoTracking().Where(b => !b.Flushed && b.DueAtUtc <= now.ToUnixTimeMilliseconds()).OrderBy(b => b.DueAtUtc).Take(32).ToArrayAsync(ct)).Select(r => JsonSerializer.Deserialize<CoreEventBucket>(r.PayloadJson, CoreEventPersistence.Json)!).ToArray(); }
    public async ValueTask CompleteBucketAsync(Guid bucketId, CancellationToken ct = default, string? code = null)
    { await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.CoreEventBuckets.SingleOrDefaultAsync(b => b.BucketId == bucketId.ToString("D"), ct);
        if (row is null || row.Flushed) return;
        var bucket = JsonSerializer.Deserialize<CoreEventBucket>(row.PayloadJson, CoreEventPersistence.Json)!;
        row.Flushed = true; row.PayloadJson = JsonSerializer.Serialize(bucket with { Flushed = true, CompletionCode = code }, CoreEventPersistence.Json);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<CoreEventReceipt>> PendingAsync(CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.CoreEvents.AsNoTracking().Where(e => !e.Snapshotted || db.CoreEventDeliveries.Any(d => d.EventId == e.EventId && (d.Status == (int)EventMatchStatus.Pending || d.Status == (int)EventMatchStatus.Matched))).OrderBy(e => e.ReceivedAtUtc).ThenBy(e => e.EventId).Take(32).ToArrayAsync(ct);
        return rows.Select(r => new CoreEventReceipt(JsonSerializer.Deserialize<CoreEventOccurrence>(r.PayloadJson, CoreEventPersistence.Json)!, DateTimeOffset.FromUnixTimeMilliseconds(r.ReceivedAtUtc), r.Snapshotted)).ToArray();
    }
    public async ValueTask SnapshotAsync(Guid eventId, IReadOnlyList<EventSubscriptionSnapshot> subscriptions, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var r = await db.CoreEvents.SingleAsync(e => e.EventId == eventId.ToString("D"), ct);
        if (r.Snapshotted) return;
        foreach (var s in subscriptions.Where(s => s.Owner.AgentInstanceId.ToString("D") == r.AgentInstanceId && s.Owner.ProfileId.ToString("D") == r.ProfileId))
            db.CoreEventDeliveries.Add(new() { EventId = r.EventId, AutomationId = s.AutomationId.ToString("D"), AgentInstanceId = r.AgentInstanceId, ProfileId = r.ProfileId, SnapshotJson = JsonSerializer.Serialize(s, CoreEventPersistence.Json) });
        r.Snapshotted = true;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
    public async ValueTask<IReadOnlyList<CoreEventDelivery>> DeliveriesAsync(Guid eventId, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.CoreEventDeliveries.AsNoTracking().Where(d => d.EventId == eventId.ToString("D")).OrderBy(d => d.AutomationId).ToArrayAsync(ct)).Select(Read).ToArray(); }
    public async ValueTask<EventFilterResult> DecideAsync(Guid eventId, Guid automationId, EventFilterResult decision, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.CoreEventDeliveries.Where(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D") && d.DecisionJson == null && d.Status == (int)EventMatchStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DecisionJson, JsonSerializer.Serialize(decision, CoreEventPersistence.Json))
                .SetProperty(d => d.Status, (int)(decision.Matched == true ? EventMatchStatus.Matched : decision.Matched == false ? EventMatchStatus.Filtered : EventMatchStatus.FilterError)).SetProperty(d => d.Code, decision.Code), ct);
        var row = await db.CoreEventDeliveries.AsNoTracking().SingleOrDefaultAsync(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D"), ct);
        return row?.DecisionJson is not null && row.Status is not ((int)EventMatchStatus.PolicySkipped or (int)EventMatchStatus.LoopSkipped or (int)EventMatchStatus.BudgetSkipped)
            ? JsonSerializer.Deserialize<EventFilterResult>(row.DecisionJson, CoreEventPersistence.Json)!
            : new(null, "error", "delivery-unavailable");
    }
    public async ValueTask FinishAsync(Guid eventId, Guid automationId, EventMatchStatus status, string? code = null, CancellationToken ct = default)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        await db.CoreEventDeliveries.Where(d => d.EventId == eventId.ToString("D") && d.AutomationId == automationId.ToString("D") && (d.Status == (int)EventMatchStatus.Pending || d.Status == (int)EventMatchStatus.Matched))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, (int)status).SetProperty(d => d.Code, code), ct);
    }
    public async ValueTask<IReadOnlyList<CoreEventDelivery>> ActivityAsync(TriggerOwner owner, CancellationToken ct = default)
    { await using var db = await contexts.CreateDbContextAsync(ct); return (await db.CoreEventDeliveries.AsNoTracking().Where(d => d.AgentInstanceId == owner.AgentInstanceId.ToString("D") && d.ProfileId == owner.ProfileId.ToString("D")).OrderByDescending(d => d.EventId).Take(100).ToArrayAsync(ct)).Select(Read).ToArray(); }
    private static CoreEventDelivery Read(CoreEventDeliveryRecord r) => new(Guid.Parse(r.EventId), JsonSerializer.Deserialize<EventSubscriptionSnapshot>(r.SnapshotJson, CoreEventPersistence.Json)!, (EventMatchStatus)r.Status, r.DecisionJson is null ? null : JsonSerializer.Deserialize<EventFilterResult>(r.DecisionJson, CoreEventPersistence.Json), r.Code);
}
