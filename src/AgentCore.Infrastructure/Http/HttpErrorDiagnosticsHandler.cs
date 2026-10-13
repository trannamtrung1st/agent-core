using System.Net;

namespace AgentCore.Infrastructure.Http;

/// <summary>Inspect an error prefix once, then replay every consumed byte to the adapter.</summary>
internal sealed class HttpErrorDiagnosticsHandler(HttpErrorDiagnostics diagnostics, string integration) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Options.Set(HttpErrorDiagnostics.ParsingEnabled, diagnostics.Enabled);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || !diagnostics.Enabled || (int)response.StatusCode < 400) return response;
        var prefix = new byte[HttpErrorDiagnostics.MaxBodyBytes + 1];
        var read = 0;
        Stream? stream = null;
        string? state = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            while (read < prefix.Length)
            {
                var count = await stream.ReadAsync(prefix.AsMemory(read), timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                read += count;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { state = "timeout"; }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { state = "unreadable"; }
        catch
        {
            response.Dispose();
            throw;
        }
        if (stream is not null)
            response.Content = new ReplayContent(response.Content, new PrefixStream(prefix.AsMemory(0, read), stream));
        request.Options.Set(HttpErrorDiagnostics.Inspection, new(prefix.AsMemory(0, read), state));
        diagnostics.Log((int)response.StatusCode, integration, prefix.AsMemory(0, read), state);
        return response;
    }

    private sealed class ReplayContent : HttpContent
    {
        private readonly HttpContent original;
        private readonly Stream stream;
        public ReplayContent(HttpContent original, Stream stream)
        {
            this.original = original;
            this.stream = stream;
            foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken) => stream.CopyToAsync(target, cancellationToken);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override bool TryComputeLength(out long length)
        {
            length = original.Headers.ContentLength ?? 0;
            return original.Headers.ContentLength.HasValue;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); original.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class PrefixStream(ReadOnlyMemory<byte> prefix, Stream tail) : Stream
    {
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (offset >= prefix.Length) return tail.Read(buffer);
            var count = Math.Min(buffer.Length, prefix.Length - offset);
            prefix.Span.Slice(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return offset < prefix.Length ? Read(buffer.Span) : await tail.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) tail.Dispose(); base.Dispose(disposing); }
    }
}
