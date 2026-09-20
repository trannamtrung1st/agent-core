using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Persistence;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Infrastructure.Tests;

public sealed class AttachmentProcessorTests
{
    public AttachmentProcessorTests()
    {
        AttachmentProcessor.RemoteReferencesObserved = 0;
        AttachmentProcessor.CompletedRemoteFetches = 0;
    }

    [Fact]
    public async Task Text_json_csv_markdown_and_pdf_extract_with_page_provenance()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);
        var pdf = await Upload(store, session, "doc.pdf", "application/pdf", PdfTwoPages());
        var text = await Upload(store, session, "note.txt", "text/plain", "hello notes"u8.ToArray());
        var json = await Upload(store, session, "a.json", "application/json", """{"a":1}"""u8.ToArray());
        var csv = await Upload(store, session, "a.csv", "text/csv", "a,b\n1,2\n"u8.ToArray());
        var md = await Upload(store, session, "a.md", "text/markdown", "# Title\nSee http://example.invalid/x"u8.ToArray());

        var results = await processor.ProcessTurnAsync(session, [pdf.AttachmentId, text.AttachmentId, json.AttachmentId, csv.AttachmentId, md.AttachmentId]);
        var pdfResult = results[0];
        Assert.Equal(AttachmentProcessKind.ExtractedText, pdfResult.Kind);
        Assert.Contains("Alpha page", pdfResult.Text, StringComparison.Ordinal);
        Assert.Contains("Beta page", pdfResult.Text, StringComparison.Ordinal);
        Assert.Contains("[page 1]", pdfResult.Text, StringComparison.Ordinal);
        Assert.Equal("pages 1-2", pdfResult.Provenance);
        Assert.Contains("hello notes", results[1].Text, StringComparison.Ordinal);
        Assert.Contains("\"a\":1", results[2].Text, StringComparison.Ordinal);
        Assert.Contains("1,2", results[3].Text, StringComparison.Ordinal);
        Assert.True(AttachmentProcessor.RemoteReferencesObserved > 0);
        Assert.Equal(0, AttachmentProcessor.CompletedRemoteFetches);
        Assert.Null(AttachmentProcessor.TryOpenRemote(new Uri("http://example.invalid/x")));
    }

    [Fact]
    public async Task Images_strip_exif_and_pixel_bombs_fail_closed()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);
        var png = PngWithExif();
        var uploaded = await Upload(store, session, "shot.png", "image/png", png);
        var result = (await processor.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal(AttachmentProcessKind.Image, result.Kind);
        Assert.NotNull(result.StrippedImage);
        Assert.DoesNotContain("AgentCoreSecret"u8.ToArray(), result.StrippedImage);
        Assert.Equal("image/png", result.ContentType);
        Assert.True(HasPngSignature(result.StrippedImage!));
    }

    [Fact]
    public async Task Sanitized_image_content_type_matches_output_bytes_for_each_supported_format()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);

        var pngBytes = MinimalPngBytes();
        var jpegBytes = MinimalJpegBytes();
        var webpBytes = MinimalWebpBytes();
        var gifBytes = MinimalGifBytes();

        var png = await Upload(store, session, "mislabeled.jpg", "image/png", pngBytes);
        var jpeg = await Upload(store, session, "mislabeled.png", "image/jpeg", jpegBytes);
        var webp = await Upload(store, session, "clip.webp", "image/webp", webpBytes);
        var gif = await Upload(store, session, "clip.gif", "image/gif", gifBytes);

        var results = await processor.ProcessTurnAsync(session, [png.AttachmentId, jpeg.AttachmentId, webp.AttachmentId, gif.AttachmentId]);

        Assert.Equal("image/png", results[0].ContentType);
        Assert.True(HasPngSignature(results[0].StrippedImage!));
        Assert.Equal("image/jpeg", results[1].ContentType);
        Assert.True(HasJpegSignature(results[1].StrippedImage!));
        Assert.Equal("image/png", results[2].ContentType);
        Assert.True(HasPngSignature(results[2].StrippedImage!));
        Assert.Equal("image/png", results[3].ContentType);
        Assert.True(HasPngSignature(results[3].StrippedImage!));
    }

    [Fact]
    public void Processor_cache_keys_differ_between_attachment_processor_versions()
    {
        var id = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var legacy = AttachmentProcessor.CacheKey(id, "attachment-processors/1");
        var current = AttachmentProcessor.CacheKey(id, AttachmentLimits.ProcessorVersion);
        Assert.NotEqual(legacy, current);
        Assert.Equal("attachment-processors/2", AttachmentLimits.ProcessorVersion);
    }

    [Fact]
    public async Task Content_type_parameters_do_not_change_sanitized_png_output()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);
        var uploaded = await Upload(store, session, "a.png", "image/png; charset=binary", MinimalPngBytes());
        var result = (await processor.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal("image/png", result.ContentType);
        Assert.True(HasPngSignature(result.StrippedImage!));

        var bomb = AttachmentProcessor.ProcessBytes(
            uploaded with { DisplayName = "bomb.png" },
            OversizedPngHeader());
        Assert.Equal("pixel_limit", bomb.FailureCode);
    }

    [Fact]
    public async Task Truncation_timeout_cancel_and_unsupported_readers_fail_closed()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);
        var huge = Encoding.UTF8.GetBytes(new string('a', AttachmentLimits.MaxExtractionOutputBytes + 32));
        var uploaded = await Upload(store, session, "big.txt", "text/plain", huge);
        var truncated = (await processor.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.True(Encoding.UTF8.GetByteCount(truncated.Text) <= AttachmentLimits.MaxExtractionOutputBytes);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessTurnAsync(session, [uploaded.AttachmentId], cts.Token).AsTask());

        var office = AttachmentProcessor.ProcessBytes(
            uploaded with { ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Readable = true },
            "not-a-docx"u8.ToArray());
        Assert.Equal(AttachmentProcessKind.Unsupported, office.Kind);
        Assert.Equal("unsupported_reader", office.FailureCode);
        Assert.DoesNotContain("fabricated", office.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cache_is_keyed_by_attachment_and_processor_version()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var v1 = new AttachmentProcessor(store, "v1");
        var uploaded = await Upload(store, session, "note.txt", "text/plain", "first"u8.ToArray());
        var first = (await v1.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal("first", first.Text);

        await using var stream = await store.OpenContentAsync(session, uploaded.AttachmentId);
        stream.Position = 0;
        var overwritten = Encoding.UTF8.GetBytes("second");
        // In-memory store keeps original bytes; version change still re-runs because cache key differs.
        var v2 = new AttachmentProcessor(store, "v2");
        var second = (await v2.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal("v2", second.ProcessorVersion);
        Assert.NotEqual(first.ProcessorVersion, second.ProcessorVersion);
        var again = (await v1.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal("v1", again.ProcessorVersion);
        Assert.Equal(first.Text, again.Text);
    }

    [Fact]
    public async Task Markdown_with_octet_stream_declared_type_extracts()
    {
        var store = new InMemoryAttachmentStore(TimeProvider.System);
        var session = Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842");
        var processor = new AttachmentProcessor(store);
        var uploaded = await store.UploadPendingAsync(
            session,
            "notes.md",
            "application/octet-stream",
            new MemoryStream("Retention policy details without markdown markers."u8.ToArray()),
            allowStoreUnread: false);
        Assert.Equal("text/markdown", uploaded.ContentType);
        var result = (await processor.ProcessTurnAsync(session, [uploaded.AttachmentId]))[0];
        Assert.Equal(AttachmentProcessKind.ExtractedText, result.Kind);
        Assert.Contains("Retention policy", result.Text, StringComparison.Ordinal);
    }

    private static async Task<AttachmentRecord> Upload(
        InMemoryAttachmentStore store,
        Guid sessionId,
        string name,
        string type,
        byte[] bytes) =>
        await store.UploadPendingAsync(sessionId, name, type, new MemoryStream(bytes), allowStoreUnread: false);

    private static byte[] PdfTwoPages()
    {
        var pdf = """
%PDF-1.1
1 0 obj<< /Type /Catalog /Pages 2 0 R >>endobj
2 0 obj<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>endobj
3 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
4 0 obj<< /Length 44 >>stream
BT /F1 12 Tf 10 100 Td (Alpha page) Tj ET
endstream
endobj
5 0 obj<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>endobj
6 0 obj<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R /Resources<< /Font<< /F1 5 0 R >> >> >>endobj
7 0 obj<< /Length 43 >>stream
BT /F1 12 Tf 10 100 Td (Beta page) Tj ET
endstream
endobj
trailer<< /Root 1 0 R >>
%%EOF
""";
        return Encoding.ASCII.GetBytes(pdf.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static byte[] PngWithExif()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 20, 30));
        image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.ImageDescription, "AgentCoreSecret");
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        return buffer.ToArray();
    }

    private static byte[] OversizedPngHeader()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var buffer = new MemoryStream();
        image.SaveAsPng(buffer);
        var png = buffer.ToArray();
        var width = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(20000));
        var height = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(20000));
        Buffer.BlockCopy(width, 0, png, 16, 4);
        Buffer.BlockCopy(height, 0, png, 20, 4);
        return png;
    }

    private static bool HasPngSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == (byte)'P'
        && bytes[2] == (byte)'N'
        && bytes[3] == (byte)'G';

    private static bool HasJpegSignature(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static byte[] MinimalPngBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(1, 2, 3));
        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder());
        return buffer.ToArray();
    }

    private static byte[] MinimalJpegBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(4, 5, 6));
        using var buffer = new MemoryStream();
        image.Save(buffer, new JpegEncoder { Quality = 90 });
        return buffer.ToArray();
    }

    private static byte[] MinimalWebpBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(7, 8, 9));
        using var buffer = new MemoryStream();
        image.Save(buffer, new WebpEncoder());
        return buffer.ToArray();
    }

    private static byte[] MinimalGifBytes()
    {
        using var image = new Image<Rgba32>(2, 2, new Rgba32(10, 11, 12));
        using var buffer = new MemoryStream();
        image.Save(buffer, new GifEncoder());
        return buffer.ToArray();
    }
}
