using System.Collections.Concurrent;

namespace AgentCore.Application.Tools;

public static class EmailSendLedger
{
    private static readonly ConcurrentDictionary<Guid, byte> Executed = new();

    public static bool TryClaim(Guid approvalId) => Executed.TryAdd(approvalId, 0);

    public static void Reset() => Executed.Clear();
}
