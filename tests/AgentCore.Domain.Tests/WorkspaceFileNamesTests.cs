using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Tests;

public sealed class WorkspaceFileNamesTests
{
    [Fact]
    public void Sanitize_and_collision_are_deterministic()
    {
        Assert.Equal("notes.txt", WorkspaceFileNames.Sanitize("../notes.txt"));
        Assert.Equal("attachment.bin", WorkspaceFileNames.Sanitize(".."));
        var first = WorkspaceFileNames.Deduplicate("notes.txt", new HashSet<string>(StringComparer.Ordinal));
        Assert.Equal("notes.txt", first);
        var second = WorkspaceFileNames.Deduplicate(
            "notes.txt",
            new HashSet<string>(StringComparer.Ordinal) { "notes.txt" });
        Assert.Equal("notes-2.txt", second);
    }
}
