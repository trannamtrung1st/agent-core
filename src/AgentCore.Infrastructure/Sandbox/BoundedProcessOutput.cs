using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace AgentCore.Infrastructure.Sandbox;

internal static class BoundedProcessOutput
{
    public static async Task<string> ReadAsync(
        Process process,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var buffer = new ArrayBufferWriter<byte>(Math.Min(maxBytes, 4096));
        var budgetExceeded = 0;

        async Task PumpAsync(Stream stream)
        {
            var chunk = new byte[4096];
            while (!linked.IsCancellationRequested)
            {
                var remaining = maxBytes - buffer.WrittenCount;
                if (remaining <= 0)
                {
                    Interlocked.Exchange(ref budgetExceeded, 1);
                    linked.Cancel();
                    TryKill(process);
                    return;
                }

                var read = await stream.ReadAsync(
                        chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                        linked.Token)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                buffer.Write(chunk.AsSpan(0, read));
                if (buffer.WrittenCount >= maxBytes)
                {
                    Interlocked.Exchange(ref budgetExceeded, 1);
                    linked.Cancel();
                    TryKill(process);
                    return;
                }
            }
        }

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput.BaseStream),
            PumpAsync(process.StandardError.BaseStream));
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), pumps).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref budgetExceeded) == 1)
        {
            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
