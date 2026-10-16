using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class AgentHomePathTests
{
    [Theory]
    [InlineData("/home")]
    [InlineData("/home/")]
    [InlineData("/home/a/../b")]
    [InlineData("/home/./a")]
    [InlineData("/home/a//b")]
    [InlineData("/home/a\\b")]
    [InlineData("/home/a\0b")]
    [InlineData("/etc/passwd")]
    [InlineData("/home-other/file")]
    public void Unsafe_paths_are_rejected(string path) => Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize(path));

    [Theory]
    [InlineData("/home/.env")]
    [InlineData("/home/runtime/a")]
    [InlineData("/home/secrets/a")]
    [InlineData("/home/user-secrets/key")]
    [InlineData("/home/appsettings.json")]
    [InlineData("/home/CON.md")]
    [InlineData("/home/a:b")]
    [InlineData("/home/a ")]
    [InlineData("/home/a.")]
    public void Ordinary_canonical_filenames_are_supported(string path) => Assert.Equal(path, AgentHomePath.Normalize(path));

    [Fact]
    public void Posix_byte_bounds_and_root_are_supported()
    {
        Assert.Equal("/home", AgentHomePath.Normalize("/home", false));
        Assert.Equal("/home/reports/store-review.md", AgentHomePath.Normalize("/home/reports/store-review.md"));
        Assert.Equal("/home/" + new string('x', 255), AgentHomePath.Normalize("/home/" + new string('x', 255)));
        Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize("/home/" + new string('x', 256)));
        Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize("/home/" + new string('é', 128)));
        var deep = "/home/" + string.Join('/', Enumerable.Repeat("x", 9));
        Assert.Equal(deep, AgentHomePath.Normalize(deep));
        Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize("/home/" + string.Join('/', Enumerable.Repeat(new string('x', 255), 16))));
    }
}
