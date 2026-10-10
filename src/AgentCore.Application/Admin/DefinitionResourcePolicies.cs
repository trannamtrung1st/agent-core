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
        "application/x-ndjson",
        "text/csv",
        "text/tab-separated-values",
        "application/yaml",
        "application/toml",
        "application/xml",
        "text/xml",
        "application/rtf",
        "application/msword",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-powerpoint",
        "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.spreadsheet",
        "application/vnd.oasis.opendocument.presentation",
        "image/gif",
        "image/bmp",
        "image/tiff",
        "image/avif",
        "audio/mpeg",
        "audio/wav",
        "audio/ogg",
        "audio/flac",
        "audio/mp4",
        "video/mp4",
        "video/webm",
        "video/quicktime",
        "image/png",
        "image/jpeg",
        "image/webp",
        "application/pdf"
    };

    private static readonly HashSet<string> TextualKnowledgeMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "text/csv",
        "text/tab-separated-values",
        "application/json",
        "application/x-ndjson",
        "application/yaml",
        "application/toml",
        "application/xml",
        "text/xml"
    };

    public static bool IsTextualKnowledgeMediaType(string mediaType) =>
        TextualKnowledgeMediaTypes.Contains(mediaType);

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
        ValidateFileName(normalized);
        if (normalized.Length > 240)
        {
            throw AgentCoreErrors.Validation("logicalPath is too long.");
        }

        return normalized;
    }

    // Uploads are inert bytes; this is a format policy, not malware detection.
    public static void ValidateFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName.Trim()).ToLowerInvariant();
        if (extension is ".exe" or ".dll" or ".com" or ".scr" or ".msi" or ".msp"
            or ".bat" or ".cmd" or ".ps1" or ".sh" or ".bash" or ".zsh"
            or ".js" or ".mjs" or ".cjs" or ".vbs" or ".vbe" or ".wsf" or ".wsh"
            or ".jar" or ".app" or ".dmg" or ".pkg" or ".deb" or ".rpm"
            or ".html" or ".htm" or ".xhtml" or ".svg" or ".hta" or ".lnk" or ".url"
            or ".docm" or ".dotm" or ".xlsm" or ".xltm" or ".xlam" or ".pptm" or ".potm" or ".ppam")
            throw AgentCoreErrors.Validation("Executable and active-content resource files are not supported.");
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
            throw AgentCoreErrors.Validation("mediaType is not supported for resources.");
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

    public static void ValidateBatchManifest(
        IReadOnlyList<AgentDefinitionDraftResource> existing,
        IReadOnlyList<PreparedBatchResource> incoming)
    {
        if (incoming.Count == 0)
        {
            throw AgentCoreErrors.Validation("Batch must include at least one resource.");
        }

        var incomingPaths = new HashSet<string>(StringComparer.Ordinal);
        var incomingIds = new HashSet<Guid>();
        foreach (var item in incoming)
        {
            ValidateContentSize(item.ByteLength);
            if (!incomingPaths.Add(item.LogicalPath))
            {
                throw AgentCoreErrors.Conflict("logicalPath is duplicated in this batch.");
            }

            if (item.ResourceId is Guid resourceId && !incomingIds.Add(resourceId))
            {
                throw AgentCoreErrors.Conflict("resourceId is duplicated in this batch.");
            }
        }

        var byId = existing.ToDictionary(row => row.ResourceId);
        var occupied = existing.ToDictionary(row => row.LogicalPath, row => row.ResourceId, StringComparer.Ordinal);
        foreach (var item in incoming)
        {
            if (item.ResourceId is not Guid resourceId || !byId.TryGetValue(resourceId, out var current))
            {
                continue;
            }

            if (occupied.TryGetValue(current.LogicalPath, out var owner) && owner == resourceId)
            {
                occupied.Remove(current.LogicalPath);
            }
        }

        foreach (var item in incoming)
        {
            var replaces = item.ResourceId is Guid resourceId && byId.ContainsKey(resourceId);
            if (occupied.TryGetValue(item.LogicalPath, out var owner)
                && (!replaces || owner != item.ResourceId))
            {
                throw AgentCoreErrors.Conflict("logicalPath is already bound on this draft.");
            }

            occupied[item.LogicalPath] = replaces ? item.ResourceId!.Value : Guid.Empty;
        }

        var replacedCount = incoming.Count(item => item.ResourceId is Guid resourceId && byId.ContainsKey(resourceId));
        var resultingCount = existing.Count - replacedCount + incoming.Count;
        if (resultingCount > AgentResourceLimits.MaxItemsPerDraft)
        {
            throw AgentCoreErrors.Validation("Draft resource count exceeds the allowed limit.");
        }

        long replacedBytes = 0;
        foreach (var item in incoming)
        {
            if (item.ResourceId is Guid resourceId && byId.TryGetValue(resourceId, out var current))
            {
                replacedBytes += current.ByteLength;
            }
        }

        var resultingBytes = existing.Sum(row => row.ByteLength) - replacedBytes + incoming.Sum(item => item.ByteLength);
        if (resultingBytes > AgentResourceLimits.MaxAggregateBytes)
        {
            throw AgentCoreErrors.Validation("Draft resource aggregate size exceeds the allowed limit.");
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
        if (!IsTextualKnowledgeMediaType(mediaType))
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
