using System.Text.RegularExpressions;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class DefinitionResourcePolicies
{
    private static readonly string[] SecretSentinels =
    [
        "OPENAI_API_KEY",
        "OPENROUTER_API_KEY",
        "AGENTCORE_OWNER_CAPABILITY",
        "sk-",
        "Bearer "
    ];

    private static readonly Regex[] EmbeddedProviderKeyPatterns =
    [
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9._\-]{20,}", RegexOptions.Compiled | RegexOptions.IgnoreCase)
    ];

    private static readonly HashSet<string> AllowedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "application/json",
        "image/png",
        "image/jpeg",
        "image/webp",
        "application/pdf"
    };

    public static string NormalizeLogicalPath(string logicalPath)
    {
        if (string.IsNullOrWhiteSpace(logicalPath))
        {
            throw AgentCoreErrors.Validation("logicalPath is required.");
        }

        var trimmed = logicalPath.Trim();
        if (trimmed.StartsWith('/') || trimmed.StartsWith('\\'))
        {
            throw AgentCoreErrors.Validation("logicalPath must be a relative POSIX path.");
        }

        if (trimmed.Contains('\\', StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("logicalPath must use forward slashes.");
        }

        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw AgentCoreErrors.Validation("logicalPath must not be empty.");
        }

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw AgentCoreErrors.Validation("logicalPath must not contain traversal segments.");
            }

            if (segment.Contains(':', StringComparison.Ordinal))
            {
                throw AgentCoreErrors.Validation("logicalPath must not contain drive or scheme segments.");
            }
        }

        var normalized = string.Join('/', segments);
        RejectForbiddenTargets(normalized);
        if (normalized.Length > 240)
        {
            throw AgentCoreErrors.Validation("logicalPath is too long.");
        }

        return normalized;
    }

    public static string NormalizeMediaType(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            throw AgentCoreErrors.Validation("mediaType is required.");
        }

        var normalized = mediaType.Trim();
        if (!string.Equals(mediaType, normalized, StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("mediaType must not contain leading or trailing whitespace.");
        }

        if (!AllowedMediaTypes.Contains(normalized))
        {
            throw AgentCoreErrors.Validation("mediaType is not supported for definition resources.");
        }

        return normalized;
    }

    public static void ValidateContentSize(long byteLength)
    {
        if (byteLength <= 0)
        {
            throw AgentCoreErrors.Validation("Resource content must not be empty.");
        }

        if (byteLength > AgentResourceLimits.MaxItemBytes)
        {
            throw AgentCoreErrors.Validation("Resource content exceeds the per-item size limit.");
        }
    }

    public static void ValidateAggregateSize(long currentAggregateBytes, long nextItemBytes, int currentCount, bool replacingSameItem)
    {
        if (!replacingSameItem && currentCount >= AgentResourceLimits.MaxItemsPerDraft)
        {
            throw AgentCoreErrors.Validation("Draft resource count exceeds the allowed limit.");
        }

        var projected = currentAggregateBytes + nextItemBytes;
        if (projected > AgentResourceLimits.MaxAggregateBytes)
        {
            throw AgentCoreErrors.Validation("Draft resource aggregate size exceeds the allowed limit.");
        }
    }

    public static void RejectSecretsInTextualContent(string mediaType, ReadOnlySpan<byte> content)
    {
        if (!mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var text = System.Text.Encoding.UTF8.GetString(content);
        RejectSecretTokens(text);
    }

    public static void RejectSecretTokens(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        foreach (var sentinel in SecretSentinels)
        {
            if (value.Contains(sentinel, StringComparison.OrdinalIgnoreCase))
            {
                throw AgentCoreErrors.Validation("Resource content contains a disallowed secret reference.");
            }
        }

        foreach (var pattern in EmbeddedProviderKeyPatterns)
        {
            if (pattern.IsMatch(value))
            {
                throw AgentCoreErrors.Validation("Resource content contains a disallowed secret reference.");
            }
        }
    }

    private static void RejectForbiddenTargets(string normalizedPath)
    {
        var lower = normalizedPath.ToLowerInvariant();
        if (lower.StartsWith(".agents", StringComparison.Ordinal)
            || lower.StartsWith("agents/", StringComparison.Ordinal)
            || lower.Contains("/.agents", StringComparison.Ordinal)
            || lower.Contains("/agents/", StringComparison.Ordinal)
            || lower.StartsWith("credentials", StringComparison.Ordinal)
            || lower.Contains("/credentials", StringComparison.Ordinal)
            || lower.StartsWith("memory", StringComparison.Ordinal)
            || lower.StartsWith("triggers", StringComparison.Ordinal)
            || lower.StartsWith("workitems", StringComparison.Ordinal)
            || lower.StartsWith("approvals", StringComparison.Ordinal))
        {
            throw AgentCoreErrors.Validation("logicalPath targets a forbidden resource area.");
        }
    }
}
