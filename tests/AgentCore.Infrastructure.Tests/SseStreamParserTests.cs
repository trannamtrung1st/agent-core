using System.Text;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed class SseStreamParserTests
{
    [Fact]
    public async Task Open_stream_with_no_bytes_hits_idle_timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var totalCts = new CancellationTokenSource();
        var parser = new SseStreamParser();
        var stream = new BlockingReadStream();
        var move = parser
            .ReadDataPayloadsAsync(stream, TimeSpan.FromSeconds(2), time, totalCts.Token, CancellationToken.None)
            .GetAsyncEnumerator();
        var moveTask = move.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<SseStreamIdleTimeoutException>(() => moveTask);
        await move.DisposeAsync();
    }

    [Fact]
    public async Task Sse_comment_bytes_reset_idle_before_timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var totalCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var parser = new SseStreamParser();
        var stream = new QueueReadStream(Encoding.UTF8.GetBytes(": keep-alive\n\n"));
        await using var move = parser
            .ReadDataPayloadsAsync(stream, TimeSpan.FromSeconds(2), time, totalCts.Token, CancellationToken.None)
            .GetAsyncEnumerator();
        var moveTask = move.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<SseStreamIdleTimeoutException>(() => moveTask);
        Assert.True(stream.BlockedOnRead);
    }

    [Fact]
    public async Task Data_payload_arrives_before_idle_deadline()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var totalCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var parser = new SseStreamParser();
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n";
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(body));
        var payloads = new List<string>();
        await foreach (var payload in parser.ReadDataPayloadsAsync(
                           stream,
                           TimeSpan.FromSeconds(30),
                           time,
                           totalCts.Token,
                           CancellationToken.None))
        {
            payloads.Add(payload);
        }

        Assert.Single(payloads);
        Assert.Contains("Hi", payloads[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Total_deadline_is_not_reported_as_idle_timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var totalCts = new CancellationTokenSource();
        using var totalTimer = time.CreateTimer(static state => ((CancellationTokenSource)state!).Cancel(), totalCts, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        var parser = new SseStreamParser();
        var stream = new BlockingReadStream();
        var move = parser
            .ReadDataPayloadsAsync(stream, TimeSpan.FromSeconds(60), time, totalCts.Token, CancellationToken.None)
            .GetAsyncEnumerator();
        var moveTask = move.MoveNextAsync().AsTask();
        time.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<SseStreamTotalTimeoutException>(() => moveTask);
        await move.DisposeAsync();
    }

    [Fact]
    public async Task User_cancellation_propagates_from_read()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        using var userCts = new CancellationTokenSource();
        using var totalCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var parser = new SseStreamParser();
        var stream = new BlockingReadStream();
        var move = parser
            .ReadDataPayloadsAsync(stream, TimeSpan.FromSeconds(30), time, totalCts.Token, userCts.Token)
            .GetAsyncEnumerator();
        var moveTask = move.MoveNextAsync().AsTask();
        await userCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => moveTask);
        await move.DisposeAsync();
    }

    private sealed class BlockingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class QueueReadStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private int _offset;
        private byte[]? _current;

        public QueueReadStream(byte[] first, byte[]? second = null)
        {
            _chunks.Enqueue(first);
            if (second is not null)
            {
                _chunks.Enqueue(second);
            }
        }

        public bool BlockedOnRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (_chunks.Count == 0)
                {
                    BlockedOnRead = true;
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                    return 0;
                }

                _current = _chunks.Dequeue();
                _offset = 0;
            }

            var remaining = _current.Length - _offset;
            if (remaining == 0)
            {
                return await ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }

            var copied = Math.Min(buffer.Length, remaining);
            _current.AsSpan(_offset, copied).CopyTo(buffer.Span);
            _offset += copied;
            return copied;
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
