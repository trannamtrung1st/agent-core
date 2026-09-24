using AgentCore.Application.Memory;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private ExplicitUserMemoryCaptureOutcome _explicitMemoryCaptureOutcome = ExplicitUserMemoryCaptureOutcome.None;

    private async Task TryCaptureExplicitUserMemoryAsync(
        ConversationEntry userEntry,
        string text,
        CancellationToken cancellationToken)
    {
        _explicitMemoryCaptureOutcome = ExplicitUserMemoryCaptureOutcome.None;
        if (_structuredMemory is null)
        {
            return;
        }

        _explicitMemoryCaptureOutcome = await ExplicitUserMemoryAdmission.TryAdmitAsync(
            _structuredMemory,
            _snapshot.Definition,
            SessionId,
            _snapshot.AgentInstanceId,
            _profile,
            _snapshot.Entries,
            userEntry.EntryId,
            text,
            _logger,
            cancellationToken).ConfigureAwait(false);
    }
}
