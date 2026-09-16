using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class AttachmentClassificationTests
{
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

    [Fact]
    public void Executables_and_archives_are_rejected()
    {
        var zip = "PK\u0003\u0004"u8.ToArray();
        var result = AttachmentClassification.Inspect(zip, zip, zip.Length, "application/zip", allowStoreUnread: true);
        Assert.False(result.Accepted);
    }
}
