namespace AgentCore.Domain.Conversation;

public static class AttachmentLimits
{
    public const int MaxPerMessage = 10;
    public const long MaxBytesEach = 25L * 1024 * 1024;
    public const long MaxBytesSession = 250L * 1024 * 1024;
    public const long MaxDecodedPixels = 32_000_000;
    public const int MaxExtractionOutputBytes = 256 * 1024;
    public const int ParserTimeoutSeconds = 10;
    public const long ParserMemoryBytes = 256L * 1024 * 1024;
    public const string ProcessorVersion = "attachment-processors/1";
    public static readonly TimeSpan PendingTtl = TimeSpan.FromHours(1);
}

public enum AttachmentState
{
    Pending,
    Bound
}

public static class AttachmentMedia
{
    public static readonly IReadOnlySet<string> SupportedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain",
        "text/markdown",
        "application/json",
        "text/csv",
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/webp",
        "image/gif"
    };

    public static bool IsImage(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

public sealed record AttachmentInspectResult(
    bool Accepted,
    string ContentType,
    bool Readable,
    string? Rejection);
