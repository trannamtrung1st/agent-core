namespace AgentCore.Domain.Conversation;

public static class ArtifactLimits
{
    public const long MaxBytesEach = 50L * 1024 * 1024;
    public const long MaxBytesSession = 250L * 1024 * 1024;
}

public static class WorkspaceFileNames
{
    public static string Sanitize(string displayName)
    {
        var name = Path.GetFileName(displayName.Replace('\\', '/').Trim());
        if (string.IsNullOrWhiteSpace(name))
        {
            return "attachment.bin";
        }

        var chars = name.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_').ToArray();
        var cleaned = new string(chars).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
        {
            return "attachment.bin";
        }

        return cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    public static string Deduplicate(string sanitized, IReadOnlySet<string> existingLowerInvariant)
    {
        if (!existingLowerInvariant.Contains(sanitized.ToLowerInvariant()))
        {
            return sanitized;
        }

        var extension = Path.GetExtension(sanitized);
        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "attachment";
        }

        for (var n = 2; n < 10_000; n++)
        {
            var candidate = $"{stem}-{n}{extension}";
            if (!existingLowerInvariant.Contains(candidate.ToLowerInvariant()))
            {
                return candidate;
            }
        }

        return $"{stem}-{Guid.CreateVersion7():N}{extension}";
    }
}
