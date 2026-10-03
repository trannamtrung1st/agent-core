using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests;

public sealed class BrowserDownloadPolicyTests
{
    [Fact]
    public void Allowlisted_types_pass_and_everything_else_is_rejected()
    {
        var pdf = BrowserDownloadPolicy.Classify("report.pdf", "%PDF-1.4\n"u8);
        var png = BrowserDownloadPolicy.Classify(
            "shot.png",
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);
        var csv = BrowserDownloadPolicy.Classify("notes.csv", "sku,name\nAC-1042,Keyboard\n"u8);
        var text = BrowserDownloadPolicy.Classify("readme.txt", "plain note\n"u8);
        var renamed = BrowserDownloadPolicy.Classify(@"..\secret.exe", "MZ-not-allowed"u8);
        var html = BrowserDownloadPolicy.Classify("page.html", "<html>no</html>"u8);
        var oversized = new byte[BrowserToolLimits.MaxDownloadBytes + 1];
        oversized[0] = (byte)'%';
        oversized[1] = (byte)'P';
        oversized[2] = (byte)'D';
        oversized[3] = (byte)'F';
        oversized[4] = (byte)'-';

        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.Equal("image/png", png.ContentType);
        Assert.Equal("text/csv", csv.ContentType);
        Assert.Equal("text/plain", text.ContentType);
        Assert.Equal("secret.exe", renamed.FileName);
        Assert.Equal("download_rejected", renamed.ErrorCode);
        Assert.Null(renamed.Bytes);
        Assert.Equal("download_rejected", html.ErrorCode);
        Assert.Equal("download_too_large", BrowserDownloadPolicy.Classify("big.pdf", oversized).ErrorCode);
        Assert.Null(BrowserDownloadPolicy.Classify("big.pdf", oversized).Bytes);
    }
}
