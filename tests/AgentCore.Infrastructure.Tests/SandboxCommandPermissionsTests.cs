using System.Diagnostics;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class SandboxCommandPermissionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shell_and_argv_keep_literal_arguments_exit_status_and_removable_nested_directories(bool argv)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "sandbox-permissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string literal = "spaces 'quotes' $HOME $(touch injected) `touch backtick`";
            const string script = "umask; mkdir -p nested/child; printf '%s' \"$1\" > nested/child/value; exit 7";
            var arguments = argv ? SandboxCommandPermissions.Argv(["/bin/sh", "-c", script, "fixture", literal])
                : SandboxCommandPermissions.Shell("umask; mkdir -p nested/child; printf '%s' 'shell literal' > nested/child/value; exit 7");
            var start = new ProcessStartInfo(arguments[0]) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(7, process.ExitCode); Assert.Empty(stderr); Assert.Equal(0, Convert.ToInt32(stdout.Trim(), 8));
            Assert.Equal(argv ? literal : "shell literal", await File.ReadAllTextAsync(Path.Combine(root, "nested/child/value")));
            foreach (var directory in new[] { "nested", "nested/child" })
                Assert.Equal(UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute,
                    File.GetUnixFileMode(Path.Combine(root, directory)) & (UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute));
            Assert.False(File.Exists(Path.Combine(root, "injected"))); Assert.False(File.Exists(Path.Combine(root, "backtick")));
        }
        finally { Directory.Delete(root, true); }
    }
}
