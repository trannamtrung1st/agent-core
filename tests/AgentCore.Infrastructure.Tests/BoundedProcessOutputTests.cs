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
        using var process = StartShell(
            "python3 -c \"import sys; sys.stdout.write('a' * 200000)\"");

        var output = await BoundedProcessOutput.ReadDetailedAsync(process, SandboxLimits.MaxOutputBytes, CancellationToken.None);
        Assert.True(output.Truncated);
        Assert.InRange(Encoding.UTF8.GetByteCount(output.Text), 0, SandboxLimits.MaxOutputBytes);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ReadAsync_bounds_concurrent_stdout_and_stderr()
    {
        using var process = StartShell(
            """
            python3 -c "import sys, threading
            def work(stream, ch):
                stream.write(ch * 100000)
                stream.flush()
            t1 = threading.Thread(target=work, args=(sys.stdout, 'O'))
            t2 = threading.Thread(target=work, args=(sys.stderr, 'E'))
            t1.start()
            t2.start()
            t1.join()
            t2.join()"
            """);

        var output = await BoundedProcessOutput.ReadAsync(process, SandboxLimits.MaxOutputBytes, CancellationToken.None);
        Assert.InRange(Encoding.UTF8.GetByteCount(output), 0, SandboxLimits.MaxOutputBytes);
        Assert.NotEmpty(output);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ReadAsync_includes_both_streams_when_budget_allows()
    {
        using var process = StartShell(
            "python3 -c \"import sys; sys.stderr.write('E' * 64); sys.stderr.flush(); sys.stdout.write('O' * 64); sys.stdout.flush()\"");

        var output = await BoundedProcessOutput.ReadAsync(process, SandboxLimits.MaxOutputBytes, CancellationToken.None);
        Assert.Contains('O', output);
        Assert.Contains('E', output);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ReadAsync_trims_partial_utf8_before_decoding()
    {
        const int maxBytes = 65_536;
        using var process = StartShell(
            $"python3 -c \"import sys; sys.stdout.buffer.write(b'a' * {maxBytes - 3} + b'\\xe2\\x82'); sys.stdout.buffer.flush()\"");

        var output = await BoundedProcessOutput.ReadAsync(process, maxBytes, CancellationToken.None);
        Assert.Equal(maxBytes - 3, output.Length);
        Assert.Equal(new string('a', maxBytes - 3), output);
        Assert.InRange(Encoding.UTF8.GetByteCount(output), 0, maxBytes);
    }

    [Fact]
    public void ValidUtf8PrefixLength_drops_incomplete_multibyte_suffix()
    {
        var bytes = Encoding.UTF8.GetBytes("aé");
        Assert.Equal(3, BoundedProcessOutput.ValidUtf8PrefixLength(bytes));
        Assert.Equal(1, BoundedProcessOutput.ValidUtf8PrefixLength(bytes.AsSpan(0, 2)));
        Assert.Equal(1, BoundedProcessOutput.ValidUtf8PrefixLength(bytes.AsSpan(0, 1)));

        var decoded = BoundedProcessOutput.DecodeBoundedUtf8(bytes.AsSpan(0, 2));
        Assert.Equal("a", decoded);
        Assert.Equal(1, Encoding.UTF8.GetByteCount(decoded));
    }

    private static Process StartShell(string command)
    {
        var process = new Process
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
        process.StartInfo.ArgumentList.Add(command);
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start shell.");
        }

        return process;
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
