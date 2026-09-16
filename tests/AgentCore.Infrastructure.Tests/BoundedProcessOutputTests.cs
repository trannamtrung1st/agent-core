using System.Diagnostics;
using System.Text;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Sandbox;

namespace AgentCore.Infrastructure.Tests;

public sealed class BoundedProcessOutputTests
{
    [Fact]
    public async Task ReadAsync_stops_at_utf8_byte_limit()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolveShell(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("python3 -c \"import sys; sys.stdout.write('a' * 200000)\"");

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start shell.");
        }

        var output = await BoundedProcessOutput.ReadAsync(process, SandboxLimits.MaxOutputBytes, CancellationToken.None);
        Assert.InRange(Encoding.UTF8.GetByteCount(output), 0, SandboxLimits.MaxOutputBytes);
        Assert.True(process.HasExited);
    }

    private static string ResolveShell()
    {
        foreach (var candidate in new[] { "/bin/sh", "sh" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return "sh";
    }
}
