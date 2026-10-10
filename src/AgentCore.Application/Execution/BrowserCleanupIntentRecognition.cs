using System.Text.RegularExpressions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Execution;

/// <summary>Bounded resource hints from trusted task input, never action authorization.</summary>
public static class BrowserCleanupIntentRecognition
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex Clauses = new(@"[.!?;\n]|\bbut\b", Options);
    private static readonly Regex Logout = new(@"\b(?:log[\s-]*(?:me\s+)?out|sign[\s-]*(?:me\s+)?(?:out|off))\b", Options);
    private static readonly Regex Close = new(@"\bclose\s+(?:(?:the|my|this|your)\s+)?browser(?:\s+window)?\b", Options);
    private static readonly Regex Negative = new(@"\b(?:don['’]?t|do\s+not|never|not|avoid|without)\b", Options);
    private static readonly Regex Ambiguous = new(@"\b(?:if|maybe|perhaps|whether|how\s+to|how\s+do|why|should\s+i|could\s+i|can\s+i|might)\b", Options);
    private static readonly Regex KeepSignedIn = new(@"\b(?:keep|leave)\s+(?:me\s+)?(?:signed|logged)\s+in\b", Options);
    private static readonly Regex LeaveOpen = new(@"\b(?:keep|leave)\s+(?:(?:the|my|this|your)\s+)?browser(?:\s+window)?\s+open\b", Options);
    private static readonly Regex EvenIfLogoutFails = new(@"\beven\s+if\s+(?:(?:the\s+)?(?:log[\s-]*out|sign[\s-]*out)|(?:logging|signing)\s+out)\s+(?:fails|failed|does\s+not\s+(?:work|succeed))\b", Options);
    private static readonly Regex ConditionalClosure = new(@"\b(?:only\s+(?:close|after|when|if)|provided|unless|after\s+(?:verified|successful)|once\s+(?:verified|signed))\b", Options);

    public static BrowserCleanupIntent From(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32768) return new(false, false);
        text = EvenIfLogoutFails.Replace(text, "");
        var logout = false; var close = false;
        var denyLogout = KeepSignedIn.IsMatch(text); var denyClose = LeaveOpen.IsMatch(text);
        foreach (var clause in Clauses.Split(text))
        {
            var logoutMatch = Logout.Match(clause); var closeMatch = Close.Match(clause);
            if (!logoutMatch.Success && !closeMatch.Success) continue;
            // Conditional, advisory or conflicting wording gets ordinary bounded capacity.
            var uncertain = Ambiguous.IsMatch(clause);
            if (logoutMatch.Success)
            {
                var negative = Negative.IsMatch(clause[..logoutMatch.Index]);
                denyLogout |= negative || uncertain; logout |= !negative && !uncertain;
            }
            if (closeMatch.Success)
            {
                var negative = Negative.IsMatch(clause[..closeMatch.Index]);
                var conditional = ConditionalClosure.IsMatch(clause);
                denyClose |= negative || uncertain || conditional; close |= !negative && !uncertain && !conditional;
            }
        }
        return new(logout && !denyLogout, close && !denyClose);
    }
}
