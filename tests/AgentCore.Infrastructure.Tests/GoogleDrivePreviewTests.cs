using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class GoogleDriveTests
{
    [Theory]
    [InlineData(3, null)] [InlineData(4, null)] [InlineData(5, null)]
    [InlineData(5, 1)] [InlineData(3, 99)]
    public async Task Preview_transfer_checks_stream_and_headers_independently_of_metadata(int size, int? contentLength)
    {
        using var fixture = new Fixture();
        var credential = new { CredentialId = Guid.NewGuid() };
        var stream = new UnseekablePreviewStream(new byte[size]);
        fixture.Transport.DownloadHandler = (_, _) =>
        {
            var content = new StreamContent(stream); content.Headers.ContentLength = contentLength;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        var provider = fixture.Provider(credential.CredentialId);
        var file = new DriveFile("file-1", "preview.xlsx", "application/octet-stream", 3, "1", null, ["root"], false);
        if (size > 4 || contentLength > 4)
        {
            var error = await Assert.ThrowsAsync<AgentCoreException>(() => provider.DownloadAsync(file, 4, default).AsTask());
            Assert.Equal("preview_too_large", error.Code); Assert.Equal(413, error.StatusCode);
            if (contentLength > 4) Assert.Equal(0, stream.Reads);
        }
        else Assert.Equal(size, (await provider.DownloadAsync(file, 4, default)).Length);
        Assert.True(stream.Disposed);

        fixture.Transport.DownloadHandler = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[5]) });
        Assert.Equal(5, (await provider.DownloadAsync(file with { ByteSize = 5 }, default)).Length);
    }

    private sealed class UnseekablePreviewStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream input = new(bytes);
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { Reads++; return input.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Reads++; return input.ReadAsync(buffer, ct); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
}
