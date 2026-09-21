using System.Collections.Concurrent;

namespace AgentCore.Application.Tools;

public static class DemoSensitiveActionStore
{
    private static readonly ConcurrentDictionary<(Guid SessionId, Guid ApprovalId), byte> Executed = new();

    public static bool TryExecute(Guid sessionId, Guid approvalId, string label, out string resultJson)
    {
        if (!Executed.TryAdd((sessionId, approvalId), 0))
        {
            resultJson = """{"error":"duplicate","message":"This approval was already executed."}""";
            return false;
        }

        resultJson = $$"""{"status":"completed","label":"{{Escape(label)}}","approvalId":"{{approvalId:D}}"}""";
        return true;
    }

    public static void Reset() => Executed.Clear();

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
