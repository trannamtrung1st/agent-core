using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.SignalR;

namespace AgentCore.Api.Realtime;

public sealed partial class TenantSessionLiveDelivery
{
    internal async ValueTask PublishSettledRequestAsync(TenantSessionRoot owner, CancellationToken ct)
    {
        // Metadata notification after a quiescent commit, with no retired Run output authority.
        await Task.WhenAll(_connections.ToArray().Where(p => p.Value.Current?.Owner == owner).Select(async pair =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token); var entered = false;
            try
            {
                await pair.Value.Gate.WaitAsync(bounded.Token).ConfigureAwait(false); entered = true;
                if (pair.Value.Closed || pair.Value.Current is not { } sub || sub.Owner != owner) return;
                var recipient = await access.AuthorizeRecipientAsync(owner.TenantId, owner.SessionId, sub.Human, bounded.Token).ConfigureAwait(false);
                if (recipient.Owner != owner || recipient.LifecycleStatus != SessionLifecycleStatus.Completed || recipient.Status != SessionStatus.Ended) return;
                var output = new StateChangedOutput(recipient.Status, recipient.Mode, null, "Idle", "Idle", false, null, LifecycleStatus: recipient.LifecycleStatus);
                await hubs.Clients.Client(pair.Key).SendAsync("SessionEvent", SessionEventMapper.Map(new(Context(owner.SessionId), null, output), sub.Attachment, ++sub.Sequence), bounded.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException or OperationCanceledException)
            { if (entered && pair.Value.Current?.Owner == owner) { RetireVoice(pair.Value.Current); pair.Value.Current = null; } }
            finally { if (entered) pair.Value.Gate.Release(); }
        })).ConfigureAwait(false);
    }
}
