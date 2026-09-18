using System.Diagnostics;
using System.Text;

namespace AgentCore.Api.Tests;

internal static class ProcessHostLauncher
{
    private static readonly SemaphoreSlim StartLock = new(1, 1);

    internal static async Task<Process> StartApiAsync(ProcessStartInfo start, TimeSpan readyTimeout)
    {
        start.Arguments = InsertNoBuild(start.Arguments);
        await StartLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var stderr = new StringBuilder();
            var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start API.");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data?.Contains("Application started", StringComparison.OrdinalIgnoreCase) == true
                    || args.Data?.Contains("Now listening", StringComparison.OrdinalIgnoreCase) == true)
                {
                    ready.TrySetResult();
                }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrEmpty(args.Data))
                {
                    stderr.AppendLine(args.Data);
                }
            };
            process.Exited += (_, _) =>
            {
                ready.TrySetException(
                    new InvalidOperationException(
                        $"API process exited before ready (code {process.ExitCode}): {stderr}"));
            };
            process.EnableRaisingEvents = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var timeout = new CancellationTokenSource(readyTimeout);
            timeout.Token.Register(() =>
                ready.TrySetException(
                    new TimeoutException($"API did not start within {readyTimeout.TotalSeconds:0}s: {stderr}")));
            await ready.Task.ConfigureAwait(false);
            return process;
        }
        finally
        {
            StartLock.Release();
        }
    }

    private static string InsertNoBuild(string arguments) =>
        arguments.Contains("--no-build", StringComparison.Ordinal)
            ? arguments
            : arguments.Replace("run ", "run --no-build ", StringComparison.Ordinal);
}
