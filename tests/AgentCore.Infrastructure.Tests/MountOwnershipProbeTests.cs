using System.Diagnostics;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class MountOwnershipProbeTests
{
    [Theory]
    [InlineData(HomeAccess.ReadWrite)]
    [InlineData(HomeAccess.ReadOnly)]
    [InlineData(HomeAccess.Disabled)]
    public async Task Shell_probe_accepts_owned_roots_and_refuses_foreign_working_or_mounted_home(HomeAccess access)
    {
        var root = Directory.CreateTempSubdirectory("mount-owner-");
        try
        {
            var working = Directory.CreateDirectory(Path.Combine(root.FullName, "working")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName;
            var script = OpenSandboxService.MountOwnershipProbe(access);
            Task<int> Probe(string workingRoot, string homeRoot) => Run(script
                .Replace("/working", "'" + workingRoot + "'")
                .Replace("/home", "'" + homeRoot + "'"));
            Assert.Equal(0, await Probe(working, home));
            // The filesystem root belongs to root, while Core requires non-root execution.
            Assert.NotEqual(0, await Probe("/", home));
            Assert.Equal(access == HomeAccess.Disabled, await Probe(working, "/") == 0);
        }
        finally { root.Delete(true); }
    }

    private static async Task<int> Run(string script)
    {
        using var process = new Process { StartInfo = new("/bin/sh") { ArgumentList = { "-ec", script } } };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
