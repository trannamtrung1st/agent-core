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

    [Theory]
    [InlineData("0:0", "700", true)]
    [InlineData("0:0", "755", true)]
    [InlineData("0:0", "777", false)]
    [InlineData("999:999", "700", false)]
    public async Task Translated_metadata_requires_attested_desktop_root_mapping_and_private_permissions(string owner, string mode, bool allowed)
    {
        // The real / root is foreign to the non-root test process. Supply only
        // deterministic container stat output; native policy still refuses it.
        var stat = $"stat() {{ case \"$2\" in '%u:%g') printf '%s' '{owner}';; '%a') printf '%s' '{mode}';; *) exit 99;; esac; }}\n";
        var desktop = OpenSandboxService.MountOwnershipProbe(HomeAccess.Disabled, trustedDesktop: true).Replace("/working", "/");
        Assert.Equal(allowed, await Run(stat + desktop) == 0);
        var native = OpenSandboxService.MountOwnershipProbe(HomeAccess.Disabled).Replace("/working", "/");
        Assert.NotEqual(0, await Run(stat + native));
    }

    private static async Task<int> Run(string script)
    {
        using var process = new Process { StartInfo = new("/bin/sh") { ArgumentList = { "-ec", script } } };
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
