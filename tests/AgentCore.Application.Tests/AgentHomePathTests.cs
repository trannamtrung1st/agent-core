using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;

namespace AgentCore.Application.Tests;

public sealed class AgentHomePathTests
{
    [Theory]
    [InlineData("/home")]
    [InlineData("/home/")]
    [InlineData("/home/a/../b")]
    [InlineData("/home/.env")]
    [InlineData("/home/runtime/a")]
    [InlineData("/home/secrets/a")]
    [InlineData("/home/user-secrets/key")]
    [InlineData("/home/appsettings.json")]
    [InlineData("/home/a\\b")]
    [InlineData("/home/CON.md")]
    [InlineData("/home/a:b")]
    [InlineData("/home/a ")]
    [InlineData("/home/a.")]
    [InlineData("/etc/passwd")]
    public void Unsafe_paths_are_rejected(string path) => Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize(path));

    [Fact]
    public void Bounded_portable_names_and_root_are_supported()
    {
        Assert.Equal("/home", AgentHomePath.Normalize("/home", false));
        Assert.Equal("/home/reports/store-review.md", AgentHomePath.Normalize("/home/reports/store-review.md"));
        Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize("/home/" + new string('x', 129)));
        Assert.Throws<AgentCoreException>(() => AgentHomePath.Normalize("/home/" + string.Join('/', Enumerable.Repeat("x", 9))));
    }
}
