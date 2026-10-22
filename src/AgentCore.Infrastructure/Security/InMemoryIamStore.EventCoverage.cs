using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask<EventCoveragePage> ListTenantEventCoverageAsync(Guid tenant, Guid instance, Guid automation,
        TenantEventReadVisibility visibility, string? cursor, int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); var after = EventCoverage.Cursor(cursor);
        return ValueTask.FromResult(WithTenantEvents(() => {
            if (automationBacking.ReadTenantAutomation(tenant, automation)?.Owner is not { } owner || owner.AgentInstanceId != instance) throw new AccessDeniedException("resource_unavailable");
            var buckets = sessionBacking.CoreEvents.TenantBuckets().Where(b => b.Subscription.Owner == owner && b.Subscription.AutomationId == automation && b.Flushed
                && string.CompareOrdinal(b.BucketId.ToString("D"), after.Bucket) >= 0).OrderBy(b => b.BucketId.ToString("D"), StringComparer.Ordinal).Take(32).ToArray();
            var rows = buckets.Select(b => (b with { Sources = b.Sources.Where(s => VisibleCoverage(s, b.Subscription, visibility)
                && (s.Key != "email.message.received" || CredentialBacking().ReadTenantCredential(tenant, AgentCore.Domain.Email.EmailEventReferences.AccountId(s.DataJson)) is { Value.Purpose: AgentCore.Domain.Credentials.CredentialPurpose.EmailAccount })).ToArray() },
                (IReadOnlyCollection<Guid>)sessionBacking.CoreEvents.TenantInspected(b.BucketId)));
            return EventCoverage.Page(rows, owner, cursor, limit, buckets.Length == 32);
        }));
    }
    private (Guid Bucket, IReadOnlyList<Guid> Sources)? PrepareTenantExperienceCoverage(AgentRun root, AgentRunCheckpoint checkpoint)
    {
        if (root.Admission.ExecutionContext?.Origin != ExecutionOrigin.CoreEvent) return null;
        lock (automationBacking.AdmissionGate) lock (sessionBacking.CoreEvents.TenantGate)
        {
            var occurrence = automationBacking.ReadTenantOccurrence(root.Owner.TenantId!.Value, root.Admission.Activation.TriggerOccurrenceId!.Value)
                ?? throw new AccessDeniedException("event_source_unavailable");
            if (TenantEventExperienceCoverageRules.Bucket(root, occurrence) is not { } id) return null;
            var bucket = sessionBacking.CoreEvents.TenantBucket(root.Owner.TenantId.Value, id) ?? throw new AccessDeniedException("event_source_unavailable");
            var acknowledged = TenantEventExperienceCoverageRules.Acknowledged(root, occurrence, bucket, checkpoint);
            return (id, acknowledged);
        }
    }
    private static bool VisibleCoverage(AgentCore.Domain.Events.EventBucketSource source, AgentCore.Domain.Events.EventSubscriptionSnapshot subscription, TenantEventReadVisibility visibility) =>
        (subscription.SourceKind == TriggerSourceKind.ApplicationEvent ? visibility.Webhooks is null || visibility.Webhooks.Contains(subscription.ResourceId!.Value)
            : visibility.CoreEvents is null || visibility.CoreEvents.Contains(source.EventId))
        && (source.Key != "email.message.received" || visibility.EmailAccounts is null || visibility.EmailAccounts.Contains(AgentCore.Domain.Email.EmailEventReferences.AccountId(source.DataJson)));
}
