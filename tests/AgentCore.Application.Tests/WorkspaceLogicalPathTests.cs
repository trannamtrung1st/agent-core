using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class WorkspaceLogicalPathTests
{
    private static readonly Guid Session = Guid.Parse("019944af-0001-7000-8000-000000000001");

    [Theory]
    [InlineData("notes.md", "/workspace/working/notes.md")]
    [InlineData("./notes.md", "/workspace/working/notes.md")]
    [InlineData("reports/summary.txt", "/workspace/working/reports/summary.txt")]
    [InlineData("draft..txt", "/workspace/working/draft..txt")]
    [InlineData("notes/../summary.txt", "/workspace/working/summary.txt")]
    [InlineData("/workspace/working/a.txt", "/workspace/working/a.txt")]
    [InlineData("/workspace/working/notes/../summary.txt", "/workspace/working/summary.txt")]
    [InlineData("/", "/")]
    [InlineData("/agent/definition.json", "/agent/definition.json")]
    public void Resolve_maps_model_paths_to_canonical_logical_paths(string raw, string expected)
    {
        Assert.True(WorkspaceLogicalPath.TryResolve(raw, Session, out var canonical, out var code, out _));
        Assert.Equal(expected, canonical);
        Assert.Empty(code);
    }

    [Fact]
    public void Resolve_rejects_paths_outside_workspace_roots()
    {
        Assert.False(WorkspaceLogicalPath.TryResolve("/etc/passwd", Session, out _, out var code, out var message));
        Assert.Equal("path_outside_workspace", code);
        Assert.Contains("approved session workspace paths", message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("notes/../../outside.txt")]
    [InlineData("/workspace/../outside.txt")]
    [InlineData("/workspace/working/../secret")]
    public void Resolve_rejects_parent_segments_that_escape_the_root(string raw)
    {
        Assert.False(WorkspaceLogicalPath.TryResolve(raw, Session, out _, out var code, out var message));
        Assert.Equal("path_outside_workspace", code);
        Assert.Contains("approved session workspace paths", message, StringComparison.OrdinalIgnoreCase);
    }
}
