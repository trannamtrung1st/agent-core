using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Ports;

public sealed record TenantMemoryOperationObservation(TenantMemoryReader Reader, TenantMemoryArguments Arguments,
    IReadOnlyList<TenantLearnedMemory> Items, PreparedMemoryOperation Value, bool HasMore, TenantLearnedMemory? ExistingResult = null);
public interface ITenantMemoryOperationStore
{
    // A generation requires the current claimed frame. Human inspection is an existing
    // approval-reader facet: the actual causal User, current raw reads and saved exact IDs
    // determine visibility, including after a source was superseded or forgotten.
    ValueTask<TenantMemoryOperationObservation> ObserveTenantMemoryOperationAsync(TenantRunReference reference, Guid? generation,
        TenantMemoryArguments arguments, PreparedMemoryOperation? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default);
}
