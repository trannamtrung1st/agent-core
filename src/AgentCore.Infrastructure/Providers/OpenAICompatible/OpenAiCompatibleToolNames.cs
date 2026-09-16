using System.Text.RegularExpressions;
using AgentCore.Application.Tools;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

internal static partial class OpenAiCompatibleToolNames
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex WireNamePattern();

    public static string ToWireName(string canonicalName) =>
        canonicalName.Replace(".", "_", StringComparison.Ordinal);

    public static string ToCanonicalName(string wireName)
    {
        foreach (var name in ToolCatalog.AllKnownNames())
        {
            if (string.Equals(ToWireName(name), wireName, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return wireName;
    }

    public static bool IsWireSafe(string wireName) => WireNamePattern().IsMatch(wireName);
}
