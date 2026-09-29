using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<string?> ApplyMemoryProposalsAsync(
        IReadOnlyList<MemoryProposal>? proposals,
        CancellationToken cancellationToken)
    {
        if (_structuredMemory is null || proposals is not { Count: > 0 })
        {
            return null;
        }

        var sourceEntry = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.User);
        var results = await MemoryAdmission.AdmitAsync(
            _structuredMemory,
            _snapshot.Definition,
            SessionId,
            _snapshot.AgentInstanceId,
            _profile,
            _snapshot.Entries,
            sourceEntry?.EntryId ?? Guid.Empty,
            proposals,
            _logger,
            cancellationToken).ConfigureAwait(false);
        return MemoryAdmissionPrompt.Render(results);
    }
}
