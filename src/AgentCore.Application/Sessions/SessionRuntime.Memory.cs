using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<string?> ApplyMemoryProposalsAsync(
        IReadOnlyList<MemoryProposal> proposals,
        CancellationToken cancellationToken)
    {
        if (_structuredMemory is null || proposals.Count == 0)
        {
            return null;
        }

        var results = await MemoryAdmission.AdmitAsync(
            _structuredMemory,
            _snapshot.Definition,
            SessionId,
            _snapshot.AgentInstanceId,
            _profile,
            _snapshot.Entries,
            UserTurnSourceEntryId(),
            proposals,
            _logger,
            cancellationToken).ConfigureAwait(false);
        return MemoryAdmissionPrompt.Render(results);
    }

    private Guid UserTurnSourceEntryId()
    {
        if (_activeResponseTriggerKind != TriggerKind.UserTurn || _activeEntryId is not { } assistantId)
        {
            return Guid.Empty;
        }

        Guid? source = null;
        foreach (var entry in _snapshot.Entries)
        {
            if (entry.EntryId == assistantId)
            {
                break;
            }

            if (entry.Role == ConversationRole.User)
            {
                source = entry.EntryId;
            }
        }

        return source ?? Guid.Empty;
    }

    private void StageMemoryProposals(IReadOnlyList<MemoryProposal>? proposals)
    {
        if (_memoryCommitSettled)
        {
            return;
        }

        if (proposals is not { Count: > 0 })
        {
            _stagedMemoryProposals = null;
            return;
        }

        var conversational = new List<MemoryProposal>(proposals.Count);
        foreach (var proposal in proposals)
        {
            if (MemoryProposalCodec.IsConversationalSource(proposal.Source))
            {
                conversational.Add(proposal);
            }
        }

        _stagedMemoryProposals = conversational.Count == 0 ? null : conversational;
    }

    private void DiscardStagedMemory()
    {
        _memoryCommitSettled = true;
        _stagedMemoryProposals = null;
    }

    private async Task CommitStagedMemoryAsync(
        EventContext context,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        if (_memoryCommitSettled)
        {
            return;
        }

        _memoryCommitSettled = true;
        var proposals = _stagedMemoryProposals;
        _stagedMemoryProposals = null;
        if (proposals is not { Count: > 0 } || _envelope is null)
        {
            return;
        }

        var note = await ApplyMemoryProposalsAsync(proposals, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(note))
        {
            return;
        }

        var display = _envelope.DisplayText.TrimEnd() + "\n" + note;
        var speechText = _envelope.SpeechText;
        if (_envelope.SpeechMode == ResponseSpeechMode.Custom && !string.IsNullOrEmpty(speechText))
        {
            speechText = speechText.TrimEnd() + "\n" + note;
        }

        _envelope = _envelope with { DisplayText = display, SpeechText = speechText };
        _accumulator.Replace(display);
        await PublishEnvelopeProgressAsync(context, responseId, finalize: false, cancellationToken)
            .ConfigureAwait(false);
    }
}
