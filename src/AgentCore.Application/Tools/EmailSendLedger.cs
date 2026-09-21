using System.Collections.Concurrent;

namespace AgentCore.Application.Tools;

public static class EmailSendLedger
{
    private enum SendClaimState
    {
        InFlight,
        Completed
    }

    private static readonly ConcurrentDictionary<Guid, SendClaimState> Claims = new();

    public static bool TryBegin(Guid approvalId)
    {
        if (Claims.TryGetValue(approvalId, out _))
        {
            return false;
        }

        return Claims.TryAdd(approvalId, SendClaimState.InFlight);
    }

    public static void Complete(Guid approvalId) => Claims[approvalId] = SendClaimState.Completed;

    public static void Abandon(Guid approvalId)
    {
        if (Claims.TryGetValue(approvalId, out var state) && state == SendClaimState.InFlight)
        {
            Claims.TryRemove(approvalId, out _);
        }
    }

    public static void Reset() => Claims.Clear();
}
