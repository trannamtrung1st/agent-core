using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<IReadOnlyList<MemoryReceipt>> ApplyMemoryProposalsAsync(
        IReadOnlyList<MemoryProposal> proposals,
        CancellationToken cancellationToken)
    {
        if (proposals.Count == 0)
        {
            return [];
        }

        if (_structuredMemory is null)
        {
            return MemoryReceiptProjection.FromResults(
                proposals.Select(proposal => new MemoryAdmissionResult(MemoryAdmissionStatus.Unavailable, proposal)).ToArray());
        }

        var results = await MemoryAdmission.AdmitAsync(
            _structuredMemory,
            ExecutionDefinition,
            SessionId,
            _snapshot.AgentInstanceId,
            _profile,
            _snapshot.Entries,
            UserTurnSourceEntryId(),
            proposals,
            _logger,
            cancellationToken).ConfigureAwait(false);
        return MemoryReceiptProjection.FromResults(results);
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

    private async Task CommitStagedMemoryAsync(CancellationToken cancellationToken)
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

        var receipts = await ApplyMemoryProposalsAsync(proposals, cancellationToken).ConfigureAwait(false);
        if (receipts.Count == 0)
        {
            return;
        }

        _envelope = _envelope with { MemoryReceipts = receipts };
    }

    private IReadOnlyList<PublicMemoryReceipt>? VisibleMemoryReceipts() =>
        PublicMemoryReceipt.Visible(_envelope?.MemoryReceipts);

    private IReadOnlyList<PublicEffectReceipt>? VisibleEffectReceipts() =>
        _envelope?.EffectReceipts is { Count: > 0 } effects
            ? effects.Select(item => new PublicEffectReceipt(item.Tool, item.Status, item.Label)).ToArray()
            : null;
}
