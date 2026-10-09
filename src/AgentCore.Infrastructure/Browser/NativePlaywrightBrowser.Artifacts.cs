using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using SkiaSharp;

namespace AgentCore.Infrastructure.Browser;

public sealed partial class NativePlaywrightBrowser
{
    private async ValueTask<BrowserScreenshotResult> CaptureViewportAsync(
        BrowserScreenshotRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return new BrowserScreenshotResult("provider_unavailable", null, 0);
        }

        var entered = false;
        try
        {
            await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            if (!IsAllowed(session, session.Page.Url))
            {
                return new BrowserScreenshotResult("target_denied", null, 0);
            }

            if (session.Dialog is not null) return new("dialog_pending", null, 0);
            var format = request.Format;
            if (format is not ("png" or "jpeg" or "webp")) return new("invalid", null, 0);
            ILocator? target = null;
            if (request.Target is not null)
            {
                try { target = await ResolveTargetAsync(session, request.Target, false, cancellationToken); }
                catch (BrowserTargetException ex) { return new(ex.Code, null, 0); }
                catch (BrowserTargetDeniedException) { return new("target_denied", null, 0); }
            }
            var secrets = await CollectSecretsAsync(session, cancellationToken);
            var redactions = 0;
            var width = session.Page.ViewportSize?.Width ?? BrowserToolLimits.MaxCaptureWidth;
            var height = session.Page.ViewportSize?.Height ?? BrowserToolLimits.MaxCaptureHeight;
            byte[] png;
            var maskedFrames = new List<IFrame>();
            try
            {
                foreach (var frame in session.Page.Frames)
                {
                    // Mask whole child frames: screenshots cannot prove that cross-origin pixels contain no secrets.
                    if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
                    maskedFrames.Add(frame);
                    redactions += await frame.EvaluateAsync<int>(
                """
                values => {
                  let count = 0;
                  const matches = text => values.some(value => value && text.includes(value));
                  const mask = rect => {
                    if (!rect.width || !rect.height) return;
                    const overlay = document.createElement('div');
                    overlay.setAttribute('data-agent-mask', '1');
                    Object.assign(overlay.style, { position: 'absolute', left: (rect.left + scrollX) + 'px', top: (rect.top + scrollY) + 'px',
                      width: rect.width + 'px', height: rect.height + 'px', background: '#111', zIndex: '2147483647' });
                    document.documentElement.appendChild(overlay); count++;
                  };
                  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
                    if (!matches(node.textContent || '')) continue;
                    const range = document.createRange(); range.selectNodeContents(node);
                    for (const rect of range.getClientRects()) mask(rect);
                  }
                  for (const input of document.querySelectorAll('input, textarea, select'))
                    if (matches(input.value || '')) mask(input.getBoundingClientRect());
                  return count;
                }
                """, secrets).WaitAsync(cancellationToken);
                    redactions += await frame.EvaluateAsync<int>(MaskSensitiveScript).WaitAsync(cancellationToken);
                }
                var frameMasks = session.Page.Locator("iframe");
                if (request.FullPage)
                {
                    var dimensions = await session.Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]").WaitAsync(cancellationToken);
                    width = dimensions[0]; height = dimensions[1];
                    if (width > 1920 || height > 12000) return new("capture_too_large", null, redactions, width, height);
                }
                if (target is null)
                    png = await session.Page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png, FullPage = request.FullPage, Scale = ScreenshotScale.Css,
                        Caret = ScreenshotCaret.Hide, Mask = [frameMasks], Timeout = TimeoutMs()
                    }).WaitAsync(cancellationToken);
                else
                    png = await target.ScreenshotAsync(new LocatorScreenshotOptions
                    { Type = ScreenshotType.Png, Scale = ScreenshotScale.Css, Caret = ScreenshotCaret.Hide,
                      Mask = [session.Page.Locator("input[type=password], input[type=hidden]"), frameMasks], Timeout = TimeoutMs() }).WaitAsync(cancellationToken);
                if (format != "png")
                {
                    using var image = SKImage.FromEncodedData(png);
                    using var output = image.Encode(format == "jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, 85);
                    png = output.ToArray();
                }
            }
            finally
            {
                foreach (var frame in maskedFrames)
                    try { await frame.EvaluateAsync(ClearMaskScript).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
            }

            _logger.LogInformation(
                "browser.screenshot redactions={RedactionCount} bytes={ByteSize} width={Width} height={Height}",
                redactions,
                png.Length,
                width,
                height);
            if (png.Length > BrowserToolLimits.MaxCaptureBytes)
            {
                return new BrowserScreenshotResult("capture_too_large", null, redactions, width, height);
            }

            return new BrowserScreenshotResult(null, png, redactions, width, height, "image/" + format);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsTimeout(ex))
        {
            return new BrowserScreenshotResult("timeout", null, 0);
        }
        catch (PlaywrightException ex)
        {
            var failed = await FailAsync(session, "capture", "capture", ex).ConfigureAwait(false);
            return new BrowserScreenshotResult(failed.ErrorCode, null, 0);
        }
        finally
        {
            if (entered)
            {
                session.Gate.Release();
            }
        }
    }

    private static bool TryAttachmentFileName(IDictionary<string, string> headers, out string fileName)
    {
        fileName = "";
        string? header = null;
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("content-disposition", StringComparison.OrdinalIgnoreCase))
            {
                header = pair.Value;
                break;
            }
        }

        if (header is null || !header.Contains("attachment", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fileName = "download";
        const string marker = "filename=";
        var index = header.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var value = header[(index + marker.Length)..].Trim().Trim('"');
            var semi = value.IndexOf(';');
            if (semi >= 0)
            {
                value = value[..semi].Trim().Trim('"');
            }

            fileName = BrowserDownloadPolicy.SanitizeFileName(value);
        }

        return true;
    }

    private static bool DeclaredOverDownloadCap(IDictionary<string, string> headers)
    {
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(pair.Value, out var length))
            {
                return length > BrowserToolLimits.MaxDownloadBytes;
            }
        }

        return false;
    }

    private static readonly HttpClient AttachmentProbe = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseDrainSize = 0
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private async Task<BrowserDownload?> TryStreamAttachmentAsync(SessionBrowser session, IRoute route, string url, int call)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs()));
        HttpResponseMessage response;
        try
        {
            using var request = AttachmentProbeRequest(route, url);
            response = await AttachmentProbe
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }

        using (response)
        {
            if (IsRedirect((int)response.StatusCode))
            {
                return null;
            }

            if (!TryAttachmentFileName(HeaderMap(response), out var downloadName))
            {
                return null;
            }

            try
            {
                return await ReadBoundedAttachmentAsync(session, response, downloadName, call, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!IsCancelled(session, call))
            {
                return new BrowserDownload("download_rejected", downloadName, null, null);
            }
        }
    }

    private async Task<BrowserDownload> ReadCappedAttachmentAsync(
        SessionBrowser session,
        IRoute route,
        string url,
        string downloadName,
        int call)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(TimeoutMs()));
        try
        {
            using var request = AttachmentProbeRequest(route, url);
            using var response = await AttachmentProbe
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            return await ReadBoundedAttachmentAsync(session, response, downloadName, call, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return new BrowserDownload("download_rejected", downloadName, null, null);
        }
    }

    private async Task<BrowserDownload> ReadBoundedAttachmentAsync(
        SessionBrowser session,
        HttpResponseMessage response,
        string downloadName,
        int call,
        CancellationToken cancellationToken)
    {
        if (DeclaredOverDownloadCap(HeaderMap(response)))
        {
            return new BrowserDownload("download_too_large", downloadName, null, null);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var limit = BrowserToolLimits.MaxDownloadBytes + 1;
        while (true)
        {
            if (IsCancelled(session, call))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                return new BrowserDownload("download_too_large", downloadName, null, null);
            }

            buffer.Write(chunk, 0, read);
        }

        return BrowserDownloadPolicy.Classify(downloadName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static HttpRequestMessage AttachmentProbeRequest(IRoute route, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var name in new[] { "cookie", "authorization", "accept" })
        {
            if (route.Request.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private static Dictionary<string, string> HeaderMap(HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in response.Headers)
        {
            map[pair.Key] = string.Join(", ", pair.Value);
        }

        foreach (var pair in response.Content.Headers)
        {
            map[pair.Key] = string.Join(", ", pair.Value);
        }

        return map;
    }

    private static bool IsDownloadStart(PlaywrightException exception) =>
        exception.Message.Contains("Download is starting", StringComparison.OrdinalIgnoreCase);

    private async Task<BrowserResult> AttachDownloadsAsync(
        SessionBrowser session,
        BrowserResult result,
        CancellationToken cancellationToken)
    {
        var downloads = await DrainDownloadsAsync(session, cancellationToken).ConfigureAwait(false);
        return downloads.Count == 0 ? result : result with { Downloads = downloads };
    }

    private async Task<IReadOnlyList<BrowserDownload>> DrainDownloadsAsync(
        SessionBrowser session,
        CancellationToken cancellationToken)
    {
        BrowserDownload[] staged;
        IDownload[] pending;
        lock (session.PopupGate)
        {
            staged = session.StagedDownloads.ToArray();
            session.StagedDownloads.Clear();
            pending = session.PendingDownloads.ToArray();
            session.PendingDownloads.Clear();
        }

        if (staged.Length == 0 && pending.Length == 0)
        {
            return [];
        }

        var results = new List<BrowserDownload>(staged.Length + pending.Length);
        results.AddRange(staged);
        foreach (var download in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ReadDownloadAsync(download, cancellationToken).ConfigureAwait(false));
        }

        return results.Select(download =>
            session.ProtectedValues.Any(value => value.Length > 0 &&
                ((download.FileName?.Contains(value, StringComparison.Ordinal) ?? false)
                 || download.Bytes is not null && download.Bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0))
                ? new BrowserDownload("download_rejected", null, null, null) : download).ToArray();
    }

    private static async Task<BrowserDownload> ReadDownloadAsync(IDownload download, CancellationToken cancellationToken)
    {
        var fileName = BrowserDownloadPolicy.SanitizeFileName(download.SuggestedFilename);
        try
        {
            await using var stream = await download.CreateReadStreamAsync().ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var limit = BrowserToolLimits.MaxDownloadBytes + 1;
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > limit)
                {
                    try
                    {
                        await download.CancelAsync().ConfigureAwait(false);
                    }
                    catch (PlaywrightException)
                    {
                    }

                    return new BrowserDownload("download_too_large", fileName, null, null);
                }

                buffer.Write(chunk, 0, read);
            }

            var classified = BrowserDownloadPolicy.Classify(fileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            try
            {
                await download.DeleteAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
            }

            return classified;
        }
        catch (PlaywrightException)
        {
            return new BrowserDownload("download_rejected", fileName, null, null);
        }
    }

    private sealed record BrowserScreenshotRequest(Guid SessionId, string Format = "png", bool FullPage = false, BrowserTarget? Target = null);
    private sealed record BrowserScreenshotResult(string? ErrorCode, byte[]? Png, int RedactionCount,
        int Width = 0, int Height = 0, string ContentType = "image/png");
}
