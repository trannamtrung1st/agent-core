using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentCore.Application.Models;

// Bounded heuristics, never task text: no names, code, URLs, keys or conversation snippets leave this projection.
public static class RoutingTaskSignals
{
    private const RegexOptions Flags = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Analysis = new(@"\b(prove|derive|debug|diagnose|compare|tradeoffs?|trade-offs?|optimi[sz]e|invariants?|deadlock|concurrency|root cause|step.by.step|reason carefully|think deeply|multi.step)\b|phân tích|chứng minh|suy luận|so sánh|tối ưu|analiza|demuestra|razona|analysez|démontr", Flags);
    private static readonly Regex Code = new(@"```|\b(code|function|algorithm|sql|regex|typescript|python|c#|javascript|refactor|compiler|unit tests?)\b|lập trình|mã nguồn|código|programm", Flags);
    private static readonly Regex Greeting = new(@"\b(hello|greet|greeting|say hi|good morning|good evening|hola|saluda|bonjour|salut)\b|xin chào|chào hỏi|lời chào", Flags);
    private static readonly Regex Lookup = new(@"\b(capital|define|definition|translate|spell|convert|what is|who is|when was|traduce|traduis)\b|thủ đô|định nghĩa|dịch sang", Flags);
    private static readonly Regex Creative = new(@"\b(poem|story|brainstorm|slogan|creative|rewrite)\b|bài thơ|câu chuyện|escribe|écris", Flags);

    public static IReadOnlyDictionary<string, string> Project(string? task, string inputMode,
        int attachmentCount = 0, bool requiresVision = false)
    {
        task ??= "";
        // Inspect at most 4096 UTF-16 characters. Do not scan attached content or fetch any new source.
        var bounded = task[..Math.Min(task.Length, 4096)];
        var analytical = Analysis.IsMatch(bounded);
        var code = Code.IsMatch(bounded);
        var kind = analytical ? "analysis" : code ? "code" : Greeting.IsMatch(bounded) ? "greeting"
            : Lookup.IsMatch(bounded) ? "lookup" : Creative.IsMatch(bounded) ? "creative" : "general";
        var demand = analytical ? "substantial" : code || kind == "creative" ? "moderate"
            : kind is "greeting" or "lookup" ? "light" : "unknown";
        return new Dictionary<string, string>(StringComparer.Ordinal) {
            ["signalSchema"] = "task-hints-v1", ["taskKind"] = kind, ["reasoningDemand"] = demand,
            ["containsCode"] = code.ToString(), ["inputCharacters"] = task.Length.ToString(CultureInfo.InvariantCulture),
            ["inputMode"] = inputMode, ["attachmentCount"] = attachmentCount.ToString(CultureInfo.InvariantCulture),
            ["requiresVision"] = requiresVision.ToString()
        };
    }
}
