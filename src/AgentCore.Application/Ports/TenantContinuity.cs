using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantHistoricalEntry(Guid EntryId, long Sequence, string Role, string Status, string Text, bool Truncated);
public sealed record TenantHistoricalSession(Guid SessionId, long Revision, long ThroughEntrySequence, string Title,
    string Summary, DateTimeOffset UpdatedAt, IReadOnlyList<TenantHistoricalEntry> Entries, bool HasMore, long? NextAfter,
    long AfterEntrySequence, int Limit, string ObservationHash);
public sealed record TenantContinuityObservation(TenantMemoryReader Reader, TenantContinuityArguments Arguments,
    IReadOnlyList<TenantHistoricalSession> Sessions, TenantMemoryOperationObservation? Memory, PreparedContinuityRead Value,
    bool HasMore, bool SourcesChanged = false);
public sealed record TenantHistoricalSource(Guid TenantId, Guid SessionId, long Revision, long ThroughEntrySequence, long AfterEntrySequence, int Limit, string ObservationHash);

public interface ITenantContinuityStore
{
    ValueTask<TenantContinuityObservation> ObserveTenantContinuityAsync(TenantRunReference reference, Guid? generation,
        TenantContinuityArguments arguments, PreparedContinuityRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
    ValueTask RequireTenantHistoricalSourcesAsync(TenantRunReference reference, IReadOnlyList<TenantHistoricalSource> sources,
        Guid? generation = null, CancellationToken ct = default);
}
