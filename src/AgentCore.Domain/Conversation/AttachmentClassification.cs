using System.Buffers.Binary;
using System.Text;

namespace AgentCore.Domain.Conversation;

public static class AttachmentClassification
{
    public static string SanitizeDisplayName(string? name)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? "file" : name.Trim();
        raw = raw.Replace('\\', '/');
        var slash = raw.LastIndexOf('/');
        if (slash >= 0)
        {
            raw = raw[(slash + 1)..];
        }

        raw = raw.Replace("\0", string.Empty, StringComparison.Ordinal);
        if (raw is "." or ".." || raw.Length == 0)
        {
            raw = "file";
        }

        return raw.Length <= SessionTitles.MaxLength ? raw : raw[..SessionTitles.MaxLength];
    }

    public static AttachmentInspectResult Inspect(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> suffix,
        long length,
        string declaredType,
        string displayName,
        bool allowStoreUnread)
    {
        if (length <= 0)
        {
            return Reject("Empty uploads are not accepted.");
        }

        if (IsExecutableOrArchive(prefix, length))
        {
            return Reject("Executable and archive types are not accepted.");
        }

        var resolved = ResolveContentType(prefix, suffix, length, declaredType, displayName);
        if (resolved is null)
        {
            if (!allowStoreUnread)
            {
                return Reject("File type is outside the supported processing set.");
            }

            return new AttachmentInspectResult(true, NormalizeDeclared(declaredType), false, null);
        }

        if (!IsGenericDeclaredType(declaredType)
            && !string.IsNullOrWhiteSpace(declaredType)
            && !DeclaredMatches(declaredType, resolved)
            && LooksLikeDifferentFamily(declaredType, resolved))
        {
            return Reject("Declared content type does not match the file signature.");
        }

        if (!AttachmentMedia.SupportedContentTypes.Contains(resolved))
        {
            if (!allowStoreUnread)
            {
                return Reject("File type is outside the supported processing set.");
            }

            return new AttachmentInspectResult(true, resolved, false, null);
        }

        if (IsTruncated(resolved, prefix, suffix, length))
        {
            return Reject("File is truncated or incomplete.");
        }

        var pixels = TryDecodedPixels(resolved, prefix);
        if (pixels is { } count && count > AttachmentLimits.MaxDecodedPixels)
        {
            return Reject("Decoded image exceeds 32 megapixels.");
        }

        return new AttachmentInspectResult(true, resolved, true, null);
    }

    public static string? InferContentTypeFromExtension(string displayName)
    {
        var name = SanitizeDisplayName(displayName);
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot >= name.Length - 1)
        {
            return null;
        }

        return name[(dot + 1)..].ToLowerInvariant() switch
        {
            "md" or "markdown" => "text/markdown",
            "txt" => "text/plain",
            "json" => "application/json",
            "csv" => "text/csv",
            "pdf" => "application/pdf",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            _ => null
        };
    }

    public static bool IsGenericDeclaredType(string declaredType)
    {
        var normalized = NormalizeDeclared(declaredType);
        return string.IsNullOrWhiteSpace(normalized)
            || string.Equals(normalized, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveContentType(
        ReadOnlySpan<byte> prefix,
        ReadOnlySpan<byte> suffix,
        long length,
        string declaredType,
        string displayName)
    {
        var sniffed = SniffContentType(prefix, suffix, length);
        var inferred = InferContentTypeFromExtension(displayName);
        if (sniffed is not null)
        {
            if (string.Equals(sniffed, "text/plain", StringComparison.OrdinalIgnoreCase)
                && string.Equals(inferred, "text/markdown", StringComparison.OrdinalIgnoreCase))
            {
                return "text/markdown";
            }

            return sniffed;
        }

        if (inferred is not null && AttachmentMedia.IsReadableText(inferred) && LooksLikeUtf8Text(prefix))
        {
            return inferred;
        }

        return inferred;
    }

    private static AttachmentInspectResult Reject(string message) =>
        new(false, "application/octet-stream", false, message);

    private static string NormalizeDeclared(string declaredType)
    {
        var type = declaredType.Split(';', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type;
    }

    private static bool DeclaredMatches(string declared, string sniffed)
    {
        var type = NormalizeDeclared(declared);
        if (string.Equals(type, sniffed, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(sniffed, "image/jpeg", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(type, "image/jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "image/pjpeg", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.Equals(sniffed, "text/markdown", StringComparison.OrdinalIgnoreCase)
            && string.Equals(type, "text/plain", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool LooksLikeDifferentFamily(string declared, string sniffed)
    {
        var type = NormalizeDeclared(declared);
        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            && !sniffed.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && sniffed.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sniffed, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool IsExecutableOrArchive(ReadOnlySpan<byte> prefix, long length)
    {
        if (prefix.Length >= 2 && prefix[0] == (byte)'M' && prefix[1] == (byte)'Z')
        {
            return true;
        }

        if (prefix.Length >= 4
            && prefix[0] == 0x7F
            && prefix[1] == (byte)'E'
            && prefix[2] == (byte)'L'
            && prefix[3] == (byte)'F')
        {
            return true;
        }

        if (prefix.Length >= 4
            && (BinaryPrimitives.ReadUInt32BigEndian(prefix) is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE))
        {
            return true;
        }

        if (prefix.Length >= 4 && prefix[0] == (byte)'P' && prefix[1] == (byte)'K' && prefix[2] is 0x03 or 0x05 or 0x07)
        {
            return true;
        }

        if (prefix.Length >= 2 && prefix[0] == 0x1F && prefix[1] == 0x8B)
        {
            return true;
        }

        if (prefix.Length >= 6
            && prefix[0] == (byte)'7'
            && prefix[1] == (byte)'z'
            && prefix[2] == 0xBC
            && prefix[3] == 0xAF
            && prefix[4] == 0x27
            && prefix[5] == 0x1C)
        {
            return true;
        }

        if (prefix.Length >= 7
            && prefix[0] == (byte)'R'
            && prefix[1] == (byte)'a'
            && prefix[2] == (byte)'r'
            && prefix[3] == (byte)'!')
        {
            return true;
        }

        if (length >= 262 && prefix.Length >= 262)
        {
            var ustar = prefix.Slice(257, 5);
            if (ustar.SequenceEqual("ustar"u8))
            {
                return true;
            }
        }

        if (prefix.Length >= 2 && prefix[0] == (byte)'#' && prefix[1] == (byte)'!')
        {
            return true;
        }

        return false;
    }

    private static string? SniffContentType(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix, long length)
    {
        if (prefix.Length >= 8 && prefix[0] == 0x89 && prefix.Slice(1, 3).SequenceEqual("PNG"u8))
        {
            return "image/png";
        }

        if (prefix.Length >= 3 && prefix[0] == 0xFF && prefix[1] == 0xD8 && prefix[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (prefix.Length >= 12
            && prefix.StartsWith("RIFF"u8)
            && prefix.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        if (prefix.Length >= 6 && (prefix.StartsWith("GIF87a"u8) || prefix.StartsWith("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (prefix.Length >= 5 && prefix.StartsWith("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (LooksLikeUtf8Text(prefix))
        {
            if (LooksLikeJson(prefix))
            {
                return "application/json";
            }

            if (LooksLikeCsv(prefix))
            {
                return "text/csv";
            }

            if (LooksLikeMarkdown(prefix))
            {
                return "text/markdown";
            }

            return "text/plain";
        }

        _ = suffix;
        _ = length;
        return null;
    }

    private static bool LooksLikeUtf8Text(ReadOnlySpan<byte> prefix)
    {
        if (prefix.IsEmpty)
        {
            return false;
        }

        var nuls = 0;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (prefix[i] == 0)
            {
                nuls++;
            }
        }

        return nuls == 0;
    }

    private static bool LooksLikeJson(ReadOnlySpan<byte> prefix)
    {
        var i = 0;
        while (i < prefix.Length && prefix[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        return i < prefix.Length && prefix[i] is (byte)'{' or (byte)'[';
    }

    private static bool LooksLikeCsv(ReadOnlySpan<byte> prefix)
    {
        var text = Encoding.UTF8.GetString(prefix);
        return text.Contains(',') && text.Contains('\n');
    }

    private static bool LooksLikeMarkdown(ReadOnlySpan<byte> prefix)
    {
        var text = Encoding.UTF8.GetString(prefix);
        return text.Contains("```", StringComparison.Ordinal)
            || text.StartsWith("# ", StringComparison.Ordinal)
            || text.Contains("\n# ", StringComparison.Ordinal);
    }

    private static bool IsTruncated(string contentType, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> suffix, long length)
    {
        if (string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            return length < 33 || !ContainsIend(suffix);
        }

        if (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return suffix.Length < 2 || suffix[^2] != 0xFF || suffix[^1] != 0xD9;
        }

        if (string.Equals(contentType, "image/gif", StringComparison.OrdinalIgnoreCase))
        {
            return suffix.Length < 1 || suffix[^1] != 0x3B;
        }

        if (string.Equals(contentType, "image/webp", StringComparison.OrdinalIgnoreCase))
        {
            if (prefix.Length < 12)
            {
                return true;
            }

            var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.Slice(4, 4));
            return riffSize + 8 != (ulong)length;
        }

        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return length < 8;
        }

        return false;
    }

    private static bool ContainsIend(ReadOnlySpan<byte> suffix)
    {
        var marker = "IEND"u8;
        for (var i = 0; i <= suffix.Length - marker.Length; i++)
        {
            if (suffix.Slice(i, marker.Length).SequenceEqual(marker))
            {
                return true;
            }
        }

        return false;
    }

    private static long? TryDecodedPixels(string contentType, ReadOnlySpan<byte> prefix)
    {
        if (string.Equals(contentType, "image/png", StringComparison.OrdinalIgnoreCase) && prefix.Length >= 24)
        {
            var width = BinaryPrimitives.ReadUInt32BigEndian(prefix.Slice(16, 4));
            var height = BinaryPrimitives.ReadUInt32BigEndian(prefix.Slice(20, 4));
            return (long)width * height;
        }

        if (string.Equals(contentType, "image/gif", StringComparison.OrdinalIgnoreCase) && prefix.Length >= 10)
        {
            var width = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(6, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(prefix.Slice(8, 2));
            return (long)width * height;
        }

        if (string.Equals(contentType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return TryJpegPixels(prefix);
        }

        return null;
    }

    private static long? TryJpegPixels(ReadOnlySpan<byte> prefix)
    {
        var i = 2;
        while (i + 9 < prefix.Length)
        {
            if (prefix[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = prefix[i + 1];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                if (i + 8 >= prefix.Length)
                {
                    return null;
                }

                var height = BinaryPrimitives.ReadUInt16BigEndian(prefix.Slice(i + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(prefix.Slice(i + 7, 2));
                return (long)width * height;
            }

            if (marker is 0xD8 or 0xD9)
            {
                i += 2;
                continue;
            }

            if (i + 3 >= prefix.Length)
            {
                return null;
            }

            var size = BinaryPrimitives.ReadUInt16BigEndian(prefix.Slice(i + 2, 2));
            i += 2 + size;
        }

        return null;
    }
}
