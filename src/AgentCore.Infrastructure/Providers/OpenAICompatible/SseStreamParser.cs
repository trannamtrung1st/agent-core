using System.Text;

namespace AgentCore.Infrastructure.Providers.OpenAICompatible;

internal sealed class SseStreamParser
{
    public const int MaxEventBytes = 1024 * 1024;

    public async IAsyncEnumerable<string> ReadDataPayloadsAsync(
        Stream stream,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var byteBuffer = new byte[1024];
        var charBuffer = new char[1024];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var eventBytes = 0;

        while (true)
        {
            var read = await stream.ReadAsync(byteBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                var flushed = decoder.GetChars(byteBuffer, 0, 0, charBuffer, 0, flush: true);
                foreach (var payload in ConsumeChars(charBuffer.AsSpan(0, flushed), line, data, ref eventBytes))
                {
                    yield return payload;
                }

                if (line.Length > 0 || data.Length > 0)
                {
                    var payload = FlushEvent(line, data);
                    if (payload is not null)
                    {
                        yield return payload;
                    }
                }

                yield break;
            }

            var chars = decoder.GetChars(byteBuffer, 0, read, charBuffer, 0, flush: false);
            foreach (var payload in ConsumeChars(charBuffer.AsSpan(0, chars), line, data, ref eventBytes))
            {
                yield return payload;
            }
        }
    }

    private static IEnumerable<string> ConsumeChars(
        ReadOnlySpan<char> chars,
        StringBuilder line,
        StringBuilder data,
        ref int eventBytes)
    {
        var payloads = new List<string>();
        foreach (var ch in chars)
        {
            if (ch == '\r')
            {
                continue;
            }

            if (ch == '\n')
            {
                eventBytes += line.Length + 1;
                EnsureBound(eventBytes);
                if (line.Length == 0)
                {
                    var payload = FlushEvent(line, data);
                    eventBytes = 0;
                    if (payload is not null)
                    {
                        payloads.Add(payload);
                    }

                    continue;
                }

                AppendField(line.ToString(), data);
                line.Clear();
                continue;
            }

            line.Append(ch);
            EnsureBound(eventBytes + line.Length);
        }

        return payloads;
    }

    private static void EnsureBound(int size)
    {
        if (size > MaxEventBytes)
        {
            throw new InvalidOperationException("SSE event exceeded 1 MiB.");
        }
    }

    private static void AppendField(string rawLine, StringBuilder data)
    {
        if (rawLine.StartsWith(':'))
        {
            return;
        }

        if (rawLine.StartsWith("data:", StringComparison.Ordinal))
        {
            var value = rawLine.Length > 5 && rawLine[5] == ' ' ? rawLine[6..] : rawLine[5..];
            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(value);
        }
    }

    private static string? FlushEvent(StringBuilder line, StringBuilder data)
    {
        if (line.Length > 0)
        {
            AppendField(line.ToString(), data);
            line.Clear();
        }

        if (data.Length == 0)
        {
            return null;
        }

        var payload = data.ToString();
        data.Clear();
        return payload;
    }
}
