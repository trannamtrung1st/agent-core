using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

namespace AgentCore.Infrastructure.Attachments;

public sealed class AttachmentProcessor : IAttachmentProcessor
{
    public static int RemoteReferencesObserved;
    public static int CompletedRemoteFetches;

    private readonly IAttachmentStore _store;
    private readonly ConcurrentDictionary<string, AttachmentProcessResult> _cache = new(StringComparer.Ordinal);

    public AttachmentProcessor(IAttachmentStore store)
        : this(store, AttachmentLimits.ProcessorVersion)
    {
    }

    public AttachmentProcessor(IAttachmentStore store, string version)
    {
        _store = store;
        Version = version;
    }

    public string Version { get; }

    public ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken = default) =>
        ProcessTurnCoreAsync(sessionId, attachmentIds, cancellationToken);

    private async ValueTask<IReadOnlyList<AttachmentProcessResult>> ProcessTurnCoreAsync(
        Guid sessionId,
        IReadOnlyList<Guid> attachmentIds,
        CancellationToken cancellationToken)
    {
        var results = new List<AttachmentProcessResult>(attachmentIds.Count);
        foreach (var id in attachmentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = await _store.GetAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                results.Add(Unsupported(id, "file", "application/octet-stream", "not_found"));
                continue;
            }

            var key = CacheKey(id, Version);
            if (_cache.TryGetValue(key, out var cached) && cached.ProcessorVersion == Version)
            {
                results.Add(cached);
                continue;
            }

            await using var content = await _store.OpenContentAsync(sessionId, id, cancellationToken).ConfigureAwait(false);
            var processed = await ProcessOneAsync(record, content, cancellationToken).ConfigureAwait(false);
            processed = processed with { ProcessorVersion = Version };
            _cache[key] = processed;
            results.Add(processed);
        }

        return results;
    }

    public static string CacheKey(Guid attachmentId, string? version = null) =>
        $"{attachmentId:D}:{version ?? AttachmentLimits.ProcessorVersion}";

    public static Stream? TryOpenRemote(Uri _) => null;

    private static void ObserveRemoteReferences(string text)
    {
        foreach (Match match in Regex.Matches(text, @"https?://[^\s)""']+", RegexOptions.IgnoreCase))
        {
            RemoteReferencesObserved++;
            _ = Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) ? TryOpenRemote(uri) : null;
        }
    }

    private static async Task<AttachmentProcessResult> ProcessOneAsync(
        AttachmentRecord record,
        Stream content,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(AttachmentLimits.ParserTimeoutSeconds));
        try
        {
            if (content.CanSeek && content.Length > AttachmentLimits.ParserMemoryBytes)
            {
                return Unsupported(record, "parser_memory");
            }

            await using var copy = new MemoryStream();
            await content.CopyToAsync(copy, timeout.Token).ConfigureAwait(false);
            var bytes = copy.ToArray();
            return ProcessBytes(record, bytes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unsupported(record, "parser_timeout");
        }
    }

    public static AttachmentProcessResult ProcessBytes(AttachmentRecord record, byte[] bytes)
    {
        if (!record.Readable)
        {
            return Unsupported(record, "unread");
        }

        var type = AttachmentMedia.NormalizeContentType(record.ContentType);
        if (AttachmentMedia.IsImage(type))
        {
            return ProcessImage(record, bytes);
        }

        if (string.Equals(AttachmentMedia.NormalizeContentType(type), "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ProcessPdf(record, bytes);
        }

        if (AttachmentMedia.IsReadableText(type))
        {
            return ProcessText(record, bytes);
        }

        return Unsupported(record, "unsupported_reader");
    }

    private static AttachmentProcessResult ProcessText(AttachmentRecord record, byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        ObserveRemoteReferences(text);
        text = Truncate(text);
        return new AttachmentProcessResult(
            record.AttachmentId,
            AttachmentLimits.ProcessorVersion,
            AttachmentProcessKind.ExtractedText,
            record.DisplayName,
            record.ContentType,
            text,
            Provenance: null,
            StrippedImage: null,
            FailureCode: null);
    }

    private static AttachmentProcessResult ProcessPdf(AttachmentRecord record, byte[] bytes)
    {
        var latin = Encoding.Latin1.GetString(bytes);
        ObserveRemoteReferences(latin);

        var pages = SplitPdfPages(latin);
        if (pages.Count == 0)
        {
            var fallback = ExtractPdfLiterals(latin);
            fallback = Truncate(fallback);
            return new AttachmentProcessResult(
                record.AttachmentId,
                AttachmentLimits.ProcessorVersion,
                AttachmentProcessKind.ExtractedText,
                record.DisplayName,
                record.ContentType,
                fallback,
                pages.Count == 0 ? "page 1" : $"pages 1-{Math.Max(1, pages.Count)}",
                null,
                null);
        }

        var builder = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            var pageText = ExtractPdfLiterals(pages[i]);
            if (pageText.Length == 0)
            {
                continue;
            }

            builder.Append("[page ").Append(i + 1).Append("] ").Append(pageText).Append('\n');
        }

        var text = Truncate(builder.ToString().TrimEnd());
        var provenance = pages.Count == 1 ? "page 1" : $"pages 1-{pages.Count}";
        return new AttachmentProcessResult(
            record.AttachmentId,
            AttachmentLimits.ProcessorVersion,
            AttachmentProcessKind.ExtractedText,
            record.DisplayName,
            record.ContentType,
            text,
            provenance,
            null,
            null);
    }

    private static List<string> SplitPdfPages(string latin)
    {
        var matches = Regex.Matches(latin, @"/Type\s*/Page(?![s])");
        if (matches.Count == 0)
        {
            return [];
        }

        var pages = new List<string>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : latin.Length;
            pages.Add(latin[start..end]);
        }

        return pages;
    }

    private static string ExtractPdfLiterals(string source)
    {
        var builder = new StringBuilder();
        foreach (Match match in Regex.Matches(source, @"\((?:\\.|[^\\)])*\)\s*Tj"))
        {
            var inner = match.Value;
            var open = inner.IndexOf('(');
            var close = inner.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                continue;
            }

            var literal = inner[(open + 1)..close]
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\(", "(", StringComparison.Ordinal)
                .Replace("\\)", ")", StringComparison.Ordinal);
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(literal);
        }

        return builder.ToString();
    }

    private static AttachmentProcessResult ProcessImage(AttachmentRecord record, byte[] bytes)
    {
        try
        {
            using var image = Image.Load(bytes);
            var pixels = (long)image.Width * image.Height;
            if (pixels > AttachmentLimits.MaxDecodedPixels)
            {
                return Unsupported(record, "pixel_limit");
            }

            image.Metadata.ExifProfile = null;
            image.Metadata.IccProfile = null;
            image.Metadata.XmpProfile = null;
            image.Metadata.IptcProfile = null;
            using var output = new MemoryStream();
            if (string.Equals(record.ContentType, "image/png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.ContentType, "image/gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(record.ContentType, "image/webp", StringComparison.OrdinalIgnoreCase))
            {
                image.Save(output, new PngEncoder());
            }
            else
            {
                image.Save(output, new JpegEncoder { Quality = 90 });
            }

            return new AttachmentProcessResult(
                record.AttachmentId,
                AttachmentLimits.ProcessorVersion,
                AttachmentProcessKind.Image,
                record.DisplayName,
                record.ContentType,
                Text: string.Empty,
                Provenance: null,
                StrippedImage: output.ToArray(),
                FailureCode: null);
        }
        catch (UnknownImageFormatException)
        {
            return Unsupported(record, "unsupported_reader");
        }
        catch (InvalidImageContentException)
        {
            return Unsupported(record, "pixel_limit");
        }
    }

    private static string Truncate(string text)
    {
        var bytes = Encoding.UTF8.GetByteCount(text);
        if (bytes <= AttachmentLimits.MaxExtractionOutputBytes)
        {
            return text;
        }

        var buffer = Encoding.UTF8.GetBytes(text);
        return Encoding.UTF8.GetString(buffer, 0, AttachmentLimits.MaxExtractionOutputBytes);
    }

    private static AttachmentProcessResult Unsupported(AttachmentRecord record, string code) =>
        Unsupported(record.AttachmentId, record.DisplayName, record.ContentType, code);

    private static AttachmentProcessResult Unsupported(Guid id, string name, string type, string code) =>
        new(
            id,
            AttachmentLimits.ProcessorVersion,
            AttachmentProcessKind.Unsupported,
            name,
            type,
            $"Attachment '{name}' could not be processed ({code}).",
            null,
            null,
            code);
}
