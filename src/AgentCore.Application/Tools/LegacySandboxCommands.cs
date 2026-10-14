using System.Globalization;

namespace AgentCore.Application.Tools;

/// <summary>Historical tool grammar only; every accepted verb uses the selected OpenSandbox backend.</summary>
public static class LegacySandboxCommands
{
    public static bool TryTranslate(string verb, IReadOnlyList<string> arguments, out string[] argv)
    {
        argv = [];
        if (arguments.Count > 64 || arguments.Any(a => a.Length > 4096 || a.Any(c => char.IsControl(c) || "`$;&|<>".Contains(c)))) return false;
        switch (verb.Trim().ToLowerInvariant())
        {
            case "echo" when arguments.All(a => !a.StartsWith('-') || a == "-n"): argv = ["echo", .. arguments]; return true;
            case "true" when arguments.Count == 0: argv = ["true"]; return true;
            case "sleep" when arguments.Count == 1 && int.TryParse(arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 5:
                argv = ["sleep", seconds.ToString(CultureInfo.InvariantCulture)]; return true;
            case "cat" when arguments.Count == 1:
                var path = arguments[0].Replace("/workspace/working", "/working", StringComparison.Ordinal);
                if (!(path.StartsWith("/working/", StringComparison.Ordinal)) || path.Split('/').Any(p => p is "." or "..")) return false;
                argv = ["cat", path]; return true;
            default: return false;
        }
    }
}
