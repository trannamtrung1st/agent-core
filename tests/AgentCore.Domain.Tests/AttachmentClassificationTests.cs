using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class AttachmentClassificationTests
{
    [Theory]
    [InlineData(new byte[] { 255, 254, 253 })]
    [InlineData(new byte[] { 1, 2, 3 })]
    public void Opaque_binary_without_null_bytes_is_not_misrepresented_as_text(byte[] bytes)
    {
        var result = AttachmentClassification.Inspect(bytes, bytes, bytes.Length, "application/octet-stream", "data.bin", true);
        Assert.True(result.Accepted);
        Assert.False(result.Readable);
    }

    [Fact]
    public void Titles_follow_one_file_many_files_and_image_conversation_rules()
    {
        Assert.Equal("notes.txt", SessionTitles.FromAttachments(["notes.txt"], imageOnly: false));
        Assert.Equal("Files: a.pdf +2", SessionTitles.FromAttachments(["a.pdf", "b.csv", "c.json"], imageOnly: false));
        Assert.Equal("photo.png", SessionTitles.FromAttachments(["photo.png"], imageOnly: true));
        Assert.Equal("Image conversation", SessionTitles.FromAttachments(["a.png", "b.jpg"], imageOnly: true));
    }

    [Fact]
    public void Display_names_drop_path_escape_segments()
    {
        Assert.Equal("safe.txt", AttachmentClassification.SanitizeDisplayName("../etc/safe.txt"));
        Assert.Equal("file", AttachmentClassification.SanitizeDisplayName(".."));
        Assert.Equal("file", AttachmentClassification.SanitizeDisplayName("/abs/path/"));
    }

    [Theory]
    [InlineData("archive.zip", "application/zip", "PK\u0003\u0004", "application/zip", false)]
    [InlineData("report.docx", "application/octet-stream", "PK\u0003\u0004", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", true)]
    [InlineData("sheet.xlsx", "application/octet-stream", "PK\u0003\u0004", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", true)]
    [InlineData("program.exe", "text/plain", "MZabc", "application/octet-stream", false)]
    public void Binary_uploads_are_stored_inertly_and_office_containers_use_their_reader(string name, string declared, string body, string type, bool readable)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var result = AttachmentClassification.Inspect(bytes, bytes, bytes.Length, declared, name, allowStoreUnread: true);
        Assert.True(result.Accepted);
        Assert.Equal(type, result.ContentType);
        Assert.Equal(readable, result.Readable);
    }

    [Theory]
    [InlineData("text/markdown", "notes.md", "Plain markdown body without hash headings.")]
    [InlineData("", "notes.md", "Plain markdown body without hash headings.")]
    [InlineData("application/octet-stream", "notes.md", "Plain markdown body without hash headings.")]
    public void Markdown_uploads_are_readable_when_extension_or_sniff_identifies_them(
        string declaredType,
        string displayName,
        string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var result = AttachmentClassification.Inspect(bytes, bytes, bytes.Length, declaredType, displayName, allowStoreUnread: false);
        Assert.True(result.Accepted);
        Assert.True(result.Readable);
        Assert.Equal("text/markdown", result.ContentType);
    }

    [Fact]
    public void Text_plain_with_charset_parameter_is_readable()
    {
        var bytes = "hello"u8.ToArray();
        var result = AttachmentClassification.Inspect(
            bytes,
            bytes,
            bytes.Length,
            "text/plain; charset=utf-8",
            "notes.txt",
            allowStoreUnread: false);
        Assert.True(result.Accepted);
        Assert.True(result.Readable);
        Assert.Equal("text/plain", result.ContentType);
    }

    [Fact]
    public void Pdf_extension_without_signature_is_rejected()
    {
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0x03 };
        var result = AttachmentClassification.Inspect(
            bytes,
            bytes,
            bytes.Length,
            "application/octet-stream",
            "fake.pdf",
            allowStoreUnread: false);
        Assert.False(result.Accepted);
    }
}
