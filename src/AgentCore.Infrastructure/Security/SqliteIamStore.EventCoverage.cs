using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;
public sealed partial class SqliteIamStore
{
    public async ValueTask<EventCoveragePage> ListTenantEventCoverageAsync(Guid tenant, Guid instance, Guid automation,
        TenantEventReadVisibility visibility, string? cursor, int limit, CancellationToken ct = default)
    {
        var after = EventCoverage.Cursor(cursor); await using var db = await contexts.CreateDbContextAsync(ct);
        var row = await db.Automations.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(a => a.TenantId == tenant && a.AutomationId == automation.ToString("D") && a.AgentInstanceId == instance.ToString("D"), ct)
            ?? throw new AccessDeniedException("resource_unavailable");
        var owner = TriggerStoreMapping.ToRegistration(row).Owner;
        var buckets = await db.CoreEventBuckets.IgnoreQueryFilters().AsNoTracking().Where(b => b.TenantId == tenant && b.AgentInstanceId == instance.ToString("D")
            && b.ExecutorPrincipalId == owner.ExecutorPrincipalId && b.AutomationId == automation.ToString("D") && b.Flushed && string.Compare(b.BucketId, after.Bucket) >= 0)
            .OrderBy(b => b.BucketId).Take(32).ToArrayAsync(ct);
        var accountIds = buckets.Select(EventBucket).SelectMany(b => b.Sources).Where(s => s.Key == "email.message.received")
            .Select(s => AgentCore.Domain.Email.EmailEventReferences.AccountId(s.DataJson).ToString("D")).Distinct().ToArray();
        var retainedAccounts = accountIds.Length == 0 ? [] : await OwnedCredentials(db, tenant).AsNoTracking().Where(c => accountIds.Contains(c.CredentialId) && c.Purpose == "EmailAccount")
            .Select(c => c.CredentialId).ToArrayAsync(ct).ConfigureAwait(false);
        var rows = buckets.Select(b => { var bucket = EventBucket(b); return (bucket with { Sources = bucket.Sources.Where(s => VisibleCoverage(s, bucket.Subscription, visibility)
            && (s.Key != "email.message.received" || retainedAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(s.DataJson).ToString("D")))).ToArray() },
            (IReadOnlyCollection<Guid>)(JsonSerializer.Deserialize<Guid[]>(b.CoverageJson) ?? [])); });
        return EventCoverage.Page(rows, owner, cursor, limit, buckets.Length == 32);
    }
    private static bool VisibleCoverage(AgentCore.Domain.Events.EventBucketSource source, AgentCore.Domain.Events.EventSubscriptionSnapshot subscription, TenantEventReadVisibility visibility) =>
        (subscription.SourceKind == TriggerSourceKind.ApplicationEvent ? visibility.Webhooks is null || visibility.Webhooks.Contains(subscription.ResourceId!.Value)
            : visibility.CoreEvents is null || visibility.CoreEvents.Contains(source.EventId))
        && (source.Key != "email.message.received" || visibility.EmailAccounts is null || visibility.EmailAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(source.DataJson)));
    private async Task AcknowledgeTenantExperienceCoverage(AgentCoreDbContext db, AgentRun run, AgentRunCheckpoint checkpoint, CancellationToken ct)
    {
        if (TenantExperienceReferences.Sources(checkpoint).Count == 0) return;
        var root = await TenantCoreEventProduction.RootAsync(db, run, ct).ConfigureAwait(false);
        if (root.Admission.ExecutionContext?.Origin != ExecutionOrigin.CoreEvent) return;
        var occurrenceRow = await db.TriggerOccurrences.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.TenantId == root.Owner.TenantId
            && o.OccurrenceId == root.Admission.Activation.TriggerOccurrenceId!.Value.ToString("D"), ct).ConfigureAwait(false);
        var occurrence = TriggerStoreMapping.ToOccurrence(occurrenceRow);
        if (TenantEventExperienceCoverageRules.Bucket(root, occurrence) is not { } id) return;
        var bucketRow = await db.CoreEventBuckets.IgnoreQueryFilters().SingleOrDefaultAsync(b => b.TenantId == root.Owner.TenantId && b.BucketId == id.ToString("D"), ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("event_source_unavailable");
        var acknowledged = TenantEventExperienceCoverageRules.Acknowledged(root, occurrence, EventBucket(bucketRow), checkpoint);
        if (acknowledged.Count == 0) return;
        bucketRow.CoverageJson = JsonSerializer.Serialize((JsonSerializer.Deserialize<Guid[]>(bucketRow.CoverageJson) ?? []).Concat(acknowledged).Distinct().Order());
    }
}
