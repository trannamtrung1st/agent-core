using System.Collections.Concurrent;

namespace AgentCore.Application.Tools;

public static class EmailSendLedger
{
    private enum SendClaimState
    {
        InFlight,
        Completed
    }

    public readonly record struct ClaimKey(Guid ResponseId, Guid ApprovalId);

    private static readonly ConcurrentDictionary<ClaimKey, SendClaimState> Claims = new();

    public static bool TryBegin(ClaimKey key)
    {
        if (Claims.TryGetValue(key, out _))
        {
            return false;
        }

        return Claims.TryAdd(key, SendClaimState.InFlight);
    }

    public static void Complete(ClaimKey key) => Claims[key] = SendClaimState.Completed;

    public static void Abandon(ClaimKey key)
    {
        if (Claims.TryGetValue(key, out var state) && state == SendClaimState.InFlight)
        {
            Claims.TryRemove(key, out _);
        }
    }

    public static void Reset() => Claims.Clear();
}
