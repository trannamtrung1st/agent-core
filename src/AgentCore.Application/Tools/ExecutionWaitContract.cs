using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tools;

public static class ExecutionWaitContract
{
    public static AgentRunWait Parse(string callId, JsonElement args, DateTimeOffset now)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Wait arguments must be an object.");
        var mode = args.GetProperty("mode").GetString();
        var duration = mode == "duration";
        var allowed = duration ? new[] { "mode", "seconds" } : new[] { "mode", "backgroundSessionIds", "until", "timeoutSeconds" };
        var names = args.EnumerateObject().Select(p => p.Name).ToArray();
        if (mode is not ("duration" or "background") || names.Distinct(StringComparer.Ordinal).Count() != names.Length
            || names.Any(p => !allowed.Contains(p))) throw new ArgumentException("Wait condition is unsupported.");
        var seconds = args.GetProperty(duration ? "seconds" : "timeoutSeconds").GetDouble();
        if (!double.IsFinite(seconds) || seconds <= 0 || seconds > AgentRunLimits.MaxWaitSeconds)
            throw new ArgumentException("Wait seconds must be greater than zero and at most 300.");
        var ids = duration ? [] : args.GetProperty("backgroundSessionIds").EnumerateArray().Select(v => v.GetGuid()).ToArray();
        var until = duration ? "all" : args.GetProperty("until").GetString();
        if (until is not ("all" or "any")) throw new ArgumentException("Wait until must be all or any.");
        var wait = new AgentRunWait(callId, duration ? AgentRunWaitMode.Duration : AgentRunWaitMode.Background,
            ids, until == "all" ? AgentRunWaitUntil.All : AgentRunWaitUntil.Any, now, now.AddSeconds(seconds));
        wait.Validate(); return wait;
    }
}
