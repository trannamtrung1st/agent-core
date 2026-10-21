using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantDeliveryReceiptRules
{
    public static ConversationEntry Apply(TenantDeliveryReceipt proof, TenantSessionRoot owner, ConversationEntry entry)
    {
        if (proof.Owner != owner || proof.EntryId == Guid.Empty || proof.ResponseId == Guid.Empty || proof.EntrySequence < 1
            || entry.EntryId != proof.EntryId || entry.Sequence != proof.EntrySequence || entry.ResponseId != proof.ResponseId
            || entry.Role != ConversationRole.Assistant
            || entry.Status is EntryStatus.Interrupted or EntryStatus.Failed)
            throw new AccessDeniedException("receipt_unavailable");
        var length = (entry.Envelope?.DisplayText ?? entry.Text).Length;
        if (proof.ReceivedEnd < 0 || proof.PublishedEnd < proof.ReceivedEnd || proof.ReceivedEnd > length)
            throw new AccessDeniedException("receipt_out_of_bounds");
        var blocks = proof.Blocks ?? [];
        if (blocks.Count > 32 || blocks.Select(b => b.BlockId).Distinct(StringComparer.Ordinal).Count() != blocks.Count)
            throw new AccessDeniedException("receipt_out_of_bounds");
        foreach (var evidence in blocks)
        {
            var block = entry.Envelope?.Blocks.SingleOrDefault(b => b.BlockId == evidence.BlockId);
            if (block is null || block.Kind != evidence.Kind || block.AttachmentId is not null
                || block.Kind is not (ResponseBlockKind.Markdown or ResponseBlockKind.ArtifactReference or ResponseBlockKind.Unknown)
                || block.Kind == ResponseBlockKind.ArtifactReference && (!Guid.TryParse(block.ArtifactId, out var id) || id != evidence.ArtifactId)
                || block.Kind != ResponseBlockKind.ArtifactReference && (evidence.ArtifactId is not null || block.ArtifactId is not null))
                throw new AccessDeniedException("receipt_unavailable");
        }
        var heard = entry.HeardTextEndExclusive;
        if (proof.Heard is { } speech)
        {
            var coordinate = entry.Envelope is { SpeechMode: ResponseSpeechMode.None } ? 0
                : SpokenOutput.ForPlayback(entry.Envelope?.SpeechText, entry.Envelope?.DisplayText ?? entry.Text).Length;
            if (entry.DeliveryMode != SessionMode.Voice || speech.Lease.Owner != owner || speech.Lease.Human != proof.Human
                || speech.TextEndExclusive < 0 || speech.TextEndExclusive > coordinate)
                throw new AccessDeniedException("receipt_out_of_bounds");
            heard = Math.Max(heard, speech.TextEndExclusive);
        }
        var envelope = entry.Envelope;
        if (blocks.Any(b => !envelope!.Blocks.Single(saved => saved.BlockId == b.BlockId).DisplayDelivered))
        {
            var ids = blocks.Select(b => b.BlockId).ToHashSet(StringComparer.Ordinal);
            envelope = envelope! with { Blocks = envelope.Blocks.Select(b => ids.Contains(b.BlockId) ? b with { DisplayDelivered = true } : b).ToArray() };
        }
        return entry with { HeardTextEndExclusive = heard, ReceivedTextEndExclusive = Math.Max(entry.ReceivedTextEndExclusive, proof.ReceivedEnd), Envelope = envelope };
    }
    public static void RequireVoiceOperation(TenantDeliveryReceipt proof, TenantVoiceLease.Operation? operation)
    {
        if (proof.Heard is { } heard && (heard.Lease.Owner != proof.Owner || heard.Lease.Human != proof.Human
            || operation?.IsHeldActiveBy(heard.Lease) != true)) throw new AccessDeniedException("voice_lease_unavailable");
    }
    public static async ValueTask RequireBlocksAsync(TenantDeliveryReceipt proof, TenantSecurityState state, IamAuthorizer iam,
        Func<Guid, CancellationToken, ValueTask<bool>> owns, CancellationToken ct)
    {
        foreach (var id in (proof.Blocks ?? []).Where(b => b.ArtifactId is not null).Select(b => b.ArtifactId!.Value).Distinct())
            if (!await owns(id, ct).ConfigureAwait(false) || !(await iam.CheckOwnedTenantAsync(state, proof.Human.PrincipalId,
                PermissionCatalog.Get("artifacts.read"), TenantArtifactRules.Scope(proof.Owner.TenantId, id), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("receipt_unavailable");
    }
    public static ConversationEntry Carry(ConversationEntry current, ConversationEntry prior)
    {
        if (prior.EntryId != current.EntryId || prior.Sequence != current.Sequence || prior.ResponseId != current.ResponseId) return current;
        var longer = (prior.Envelope?.DisplayText ?? prior.Text).Length > (current.Envelope?.DisplayText ?? current.Text).Length;
        var envelope = longer ? prior.Envelope : current.Envelope;
        if (envelope is null && prior.Envelope is { } previous && previous.DisplayText == current.Text && previous.Blocks.Any(b => b.DisplayDelivered))
            envelope = previous;
        if (envelope is not null && prior.Envelope is { } saved)
        {
            var blocks = envelope.Blocks.Select(block => saved.Blocks.Any(b => b.DisplayDelivered && b with { DisplayDelivered = false } == block with { DisplayDelivered = false })
                ? block with { DisplayDelivered = true } : block).ToArray();
            if (envelope.DisplayText == saved.DisplayText)
                blocks = blocks.Concat(saved.Blocks.Where(b => b.DisplayDelivered && !blocks.Any(currentBlock => currentBlock.BlockId == b.BlockId))).ToArray();
            if (!blocks.SequenceEqual(envelope.Blocks)) envelope = envelope with { Blocks = blocks };
        }
        return current with { HeardTextEndExclusive = Math.Max(current.HeardTextEndExclusive, prior.HeardTextEndExclusive), ReceivedTextEndExclusive = Math.Max(current.ReceivedTextEndExclusive, prior.ReceivedTextEndExclusive),
            Text = longer ? prior.Text : current.Text, Envelope = envelope };
    }
}
