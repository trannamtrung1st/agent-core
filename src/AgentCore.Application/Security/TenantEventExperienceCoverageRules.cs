using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

// Acknowledgement is derived from retained validated evidence, never an attempted
// tool call or a model-supplied event/bucket identity. The checkpoint owner commits it.
public static class TenantEventExperienceCoverageRules
{
    public static Guid? Bucket(AgentRun root, TriggerOccurrence occurrence)
    {
        if (root.Admission.ExecutionContext?.Origin != ExecutionOrigin.CoreEvent) return null;
        if (occurrence.Owner != TriggerOwner.ForTenant(root.Owner.TenantId!.Value, root.AgentInstanceId, root.Owner.ExecutorPrincipalId!.Value)
            || occurrence.AcceptedAgentRunId != root.AgentRunId || occurrence.ExecutionSessionId != root.SessionId
            || occurrence.OccurrenceId != root.Admission.Activation.TriggerOccurrenceId || occurrence.SourceKind != TriggerSourceKind.CoreEvent
            || occurrence.Disposition != OccurrenceRoutingDisposition.AcceptedDurable) throw new AccessDeniedException("event_source_unavailable");
        using var evidence = JsonDocument.Parse(occurrence.EvidenceJson);
        var source = evidence.RootElement.GetProperty("triggerContext").GetProperty("admissionSource").Deserialize<PreparedEventAdmissionSource>()
            ?? throw new AccessDeniedException("event_source_unavailable");
        if (source.TenantId != root.Owner.TenantId || source.TriggerId != occurrence.TriggerId || source.SourceKind != occurrence.SourceKind
            || source.SourceId != occurrence.SourceEventId || source.DedupeKey != occurrence.DedupeKey) throw new AccessDeniedException("event_source_unavailable");
        return source.IsBucket ? source.SourceId : null;
    }
    public static IReadOnlyList<Guid> Acknowledged(AgentRun root, TriggerOccurrence occurrence, CoreEventBucket bucket, AgentRunCheckpoint checkpoint)
    {
        if (Bucket(root, occurrence) != bucket.BucketId || bucket.Subscription.Owner != occurrence.Owner
            || bucket.Subscription.AutomationId != occurrence.AutomationId || bucket.Subscription.TriggerId != occurrence.TriggerId
            || !bucket.Flushed || bucket.CompletionCode is not null || bucket.ReservedOccurrenceId != occurrence.OccurrenceId)
            throw new AccessDeniedException("event_source_unavailable");
        var runs = TenantExperienceReferences.Sources(checkpoint).Where(s => s.TenantId == root.Owner.TenantId && s.Witness.Kind == ExperienceSourceKind.AgentRun)
            .Select(s => s.Witness.SourceId).ToHashSet();
        return bucket.Sources.Where(s => s.Owner == occurrence.Owner && s.Key is "run.completed" or "run.failed" && SourceRun(s) is { } run && runs.Contains(run))
            .Select(s => s.EventId).Distinct().ToArray();
    }
    private static Guid? SourceRun(AgentCore.Domain.Events.EventBucketSource source)
    {
        using var data = JsonDocument.Parse(source.DataJson);
        return data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty("agentRunId", out var id)
            && id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var run) ? run : null;
    }
}
