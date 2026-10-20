using System.Text.Json.Serialization;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

// A witness over the existing receipt/delivery or coalescing-bucket owner. It is
// neither an event credential nor permission to consume private source data.
public sealed class PreparedEventAdmissionSource
{
    [JsonConstructor]
    public PreparedEventAdmissionSource(Guid tenantId, Guid triggerId, TriggerSourceKind sourceKind, Guid sourceId,
        bool isBucket, string sourceHash, IReadOnlyList<Guid> eventIds, long? resourceRevision = null, Guid? resourceId = null)
    {
        if (tenantId == Guid.Empty || triggerId == Guid.Empty || sourceId == Guid.Empty
            || sourceKind is not (TriggerSourceKind.CoreEvent or TriggerSourceKind.ApplicationEvent)
            || sourceHash is not { Length: 64 } || sourceHash.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            || eventIds is null || eventIds.Count is < 1 or > TriggerLimits.MaxEventBucketSources || eventIds.Any(id => id == Guid.Empty)
            || (sourceKind == TriggerSourceKind.ApplicationEvent ? resourceRevision is null or < 1 || resourceId is null || resourceId == Guid.Empty : resourceRevision is not null || resourceId is not null)
            || eventIds.Distinct().Count() != eventIds.Count || !isBucket && (eventIds.Count != 1 || eventIds[0] != sourceId))
            throw new ArgumentException("An exact bounded tenant event source witness is required.");
        TenantId = tenantId; TriggerId = triggerId; SourceKind = sourceKind; SourceId = sourceId; IsBucket = isBucket;
        SourceHash = sourceHash; ResourceRevision = resourceRevision; ResourceId = resourceId; EventIds = Array.AsReadOnly(eventIds.ToArray());
    }
    public Guid TenantId { get; }
    public Guid TriggerId { get; }
    public TriggerSourceKind SourceKind { get; }
    public Guid SourceId { get; }
    public bool IsBucket { get; }
    public string SourceHash { get; }
    public IReadOnlyList<Guid> EventIds { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ResourceRevision { get; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ResourceId { get; }
    public string DedupeKey => IsBucket ? $"bucket:{SourceId:D}" : $"{(SourceKind == TriggerSourceKind.CoreEvent ? "core" : "event")}:{SourceId:D}:{TriggerId:D}";
}
