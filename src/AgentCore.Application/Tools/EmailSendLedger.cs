using System.Collections.Concurrent;

namespace AgentCore.Application.Tools;

public static class EmailSendLedger
{
    private enum SendClaimState
    {
        InFlight,
        Sent,
        DefinitelyFailed,
        Indeterminate
    }

    public readonly record struct ClaimKey(Guid ResponseId, Guid ApprovalId);

    private static readonly ConcurrentDictionary<ClaimKey, SendClaimState> Claims = new();

    public static bool TryBegin(ClaimKey key)
    {
        while (true)
        {
            if (!Claims.TryGetValue(key, out var state))
            {
                return Claims.TryAdd(key, SendClaimState.InFlight);
            }

            if (state != SendClaimState.DefinitelyFailed)
            {
                return false;
            }

            if (Claims.TryUpdate(key, SendClaimState.InFlight, SendClaimState.DefinitelyFailed))
            {
                return true;
            }
        }
    }

    public static void CompleteSent(ClaimKey key) => Claims[key] = SendClaimState.Sent;

    public static void MarkDefinitelyFailed(ClaimKey key)
    {
        if (Claims.TryGetValue(key, out var state) && state == SendClaimState.InFlight)
        {
            Claims[key] = SendClaimState.DefinitelyFailed;
        }
    }

    public static void MarkIndeterminate(ClaimKey key)
    {
        if (Claims.TryGetValue(key, out var state) && state == SendClaimState.InFlight)
        {
            Claims[key] = SendClaimState.Indeterminate;
        }
    }

    public static void Reset() => Claims.Clear();
}
