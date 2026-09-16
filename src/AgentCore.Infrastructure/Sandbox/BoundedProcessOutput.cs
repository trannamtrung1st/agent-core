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
        var gate = new object();
        var budgetExceeded = 0;

        void MarkExceeded()
        {
            Interlocked.Exchange(ref budgetExceeded, 1);
            linked.Cancel();
            TryKill(process);
        }

        async Task PumpAsync(Stream stream)
        {
            var chunk = new byte[4096];
            while (!linked.IsCancellationRequested)
            {
                int take;
                lock (gate)
                {
                    if (Volatile.Read(ref budgetExceeded) != 0)
                    {
                        take = 0;
                    }
                    else
                    {
                        var remaining = maxBytes - buffer.WrittenCount;
                        take = remaining <= 0 ? 0 : Math.Min(chunk.Length, remaining);
                        if (take <= 0)
                        {
                            Interlocked.Exchange(ref budgetExceeded, 1);
                        }
                    }
                }

                if (take <= 0)
                {
                    MarkExceeded();
                    return;
                }

                var read = await stream.ReadAsync(chunk.AsMemory(0, take), linked.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                var exceeded = false;
                lock (gate)
                {
                    var remaining = maxBytes - buffer.WrittenCount;
                    var append = Math.Min(read, remaining);
                    if (append > 0)
                    {
                        buffer.Write(chunk.AsSpan(0, append));
                    }

                    if (append < read || buffer.WrittenCount >= maxBytes)
                    {
                        Interlocked.Exchange(ref budgetExceeded, 1);
                        exceeded = true;
                    }
                }

                if (exceeded)
                {
                    MarkExceeded();
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

        return DecodeBoundedUtf8(buffer.WrittenSpan);
    }

    internal static string DecodeBoundedUtf8(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty
            ? string.Empty
            : Encoding.UTF8.GetString(bytes[..ValidUtf8PrefixLength(bytes)]);

    internal static int ValidUtf8PrefixLength(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return 0;
        }

        var length = bytes.Length;
        while (length > 0 && (bytes[length - 1] & 0xC0) == 0x80)
        {
            length--;
        }

        if (length == 0)
        {
            return 0;
        }

        var lead = bytes[length - 1];
        var expected = lead switch
        {
            < 0x80 => 1,
            _ when (lead & 0xE0) == 0xC0 => 2,
            _ when (lead & 0xF0) == 0xE0 => 3,
            _ when (lead & 0xF8) == 0xF0 => 4,
            _ => 1
        };

        var available = bytes.Length - (length - 1);
        return available >= expected ? bytes.Length : length - 1;
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
