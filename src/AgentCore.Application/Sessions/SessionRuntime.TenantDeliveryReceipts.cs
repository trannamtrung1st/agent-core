using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ConversationEntry> _tenantReceiptEvidence = new();
    private sealed record TenantReceiptReceived(EventContext Context, TenantDeliveryReceipt Receipt,
        TaskCompletionSource<bool> Completed) : SessionInput(Context);

    public async Task<bool> ReceiveTenantDeliveryAsync(TenantDeliveryReceipt proof, CancellationToken ct = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new TenantReceiptReceived(NewContext(), proof, completed), urgent: false)) return false;
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }
    private async Task HandleTenantReceiptAsync(TenantReceiptReceived input, CancellationToken ct)
    {
        try
        {
            var proof = input.Receipt;
            if (_runtimeOwner is not { } owner || owner.TenantId != proof.Owner.TenantId || owner.AgentInstanceId != proof.Owner.AgentInstanceId
                || owner.ExecutorPrincipalId != proof.Owner.ExecutorPrincipalId || SessionId != proof.Owner.SessionId
                || _store is not ITenantRuntimeReceiptWriter) throw new AccessDeniedException("receipt_unavailable");
            if (_tenantCancellationCommitted || _tenantApprovalPending || _tenantApprovalSuspended)
            { input.Completed.TrySetResult(false); return; }
            if (_activeEntryId is not null && !_responseTerminal) UpdateStreamingAssistant();
            var entry = _snapshot.Entries.SingleOrDefault(e => e.EntryId == proof.EntryId);
            if (entry is null)
                entry = (await _store.ReadHistoryPageAsync(SessionId, proof.EntrySequence - 1, null, 1, ct).ConfigureAwait(false))?.Items.SingleOrDefault();
            if (entry is null) throw new AccessDeniedException("receipt_unavailable");
            var updated = TenantDeliveryReceiptRules.Apply(proof, proof.Owner, entry);
            var proposed = _snapshot with { Entries = _snapshot.Entries.Where(e => e.EntryId != updated.EntryId).Append(updated).OrderBy(e => e.Sequence).ToArray() };
            // Credit is adopted only after the authority-checked writer commits, never when a receipt is queued.
            RequestPersist(proposed, ended: input.Completed, tenantReceipt: proof);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
    private SessionSnapshot CarryTenantReceipts(SessionSnapshot proposed, SessionSnapshot durable)
    {
        var saved = durable.Entries.ToDictionary(e => e.EntryId);
        foreach (var entry in _tenantReceiptEvidence.Values)
            if (!saved.TryGetValue(entry.EntryId, out var prior) || prior.ReceivedTextEndExclusive < entry.ReceivedTextEndExclusive) saved[entry.EntryId] = entry;
        return proposed with { Entries = proposed.Entries.Select(entry =>
        {
            if (!saved.TryGetValue(entry.EntryId, out var prior) || prior.ResponseId != entry.ResponseId || prior.Sequence != entry.Sequence
                || prior.ReceivedTextEndExclusive <= entry.ReceivedTextEndExclusive) return entry;
            var longer = (prior.Envelope?.DisplayText ?? prior.Text).Length > (entry.Envelope?.DisplayText ?? entry.Text).Length;
            return entry with { ReceivedTextEndExclusive = prior.ReceivedTextEndExclusive,
                Text = longer ? prior.Text : entry.Text, Envelope = longer ? prior.Envelope : entry.Envelope };
        }).ToArray() };
    }
}
