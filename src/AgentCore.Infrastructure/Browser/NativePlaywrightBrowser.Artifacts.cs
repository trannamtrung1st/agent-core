using System.Collections.Concurrent;
using System.Diagnostics;
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
        var privacy = _capturePolicy;
        if (privacy.Mode == BrowserScreenshotPrivacyMode.Disabled) return new("forbidden", null, 0);
        await using var interactive = await EnterInteractiveAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        if (!IsAvailable || !_sessions.TryGetValue(request.SessionId, out var session))
        {
            return new BrowserScreenshotResult("provider_unavailable", null, 0);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, CaptureLifetime);
        deadline.CancelAfter(OperationTimeout);
        var ct = deadline.Token;
        var entered = false;
        try
        {
            await session.Gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            if (!IsAllowed(session, session.Page.Url))
            {
                return new BrowserScreenshotResult("target_denied", null, 0);
            }

            if (session.Dialog is not null) return new("dialog_pending", null, 0);
            // Failed/partial captures cannot retain authority from an earlier image.
            session.VisualSnapshotId = null;
            var settled = await BrowserPageSettle.WaitAsync(session.Page, _policy.Limits.AutomaticSettleMs, ct);
            var generation = session.Generation;
            var page = session.Page;
            var unmasked = privacy.IsUnmasked(page.Url);
            var visualState = await ReadVisualStateAsync(page, ct);
            // Fence the whole combined observation, including asynchronous semantic reads.
            var stage = Stopwatch.StartNew();
            BrowserSnapshot observation;
            using (var semanticDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Optional semantic reads cannot consume the entire capture deadline.
                semanticDeadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Min(1000, OperationTimeout.TotalMilliseconds / 4)));
                try { observation = (await CaptureAsync(session, request.SessionId, semanticDeadline.Token)) with { Settled = settled }; }
                catch (Exception ex) when (!ct.IsCancellationRequested && ex is PlaywrightException or TimeoutException or OperationCanceledException)
                {
                    // No unverified URL, title or semantic text is projected. Privacy inspection
                    // below remains mandatory and uses the original operation deadline.
                    observation = new("", "", "", false, [], Settled: settled,
                        SnapshotId: "snap_" + Guid.NewGuid().ToString("N"), TabRef: FindPageId(session), ObservationUnavailable: true);
                }
            }
            var semanticMs = stage.Elapsed.TotalMilliseconds;
            stage.Restart();
            var format = request.Format;
            if (format is not ("png" or "jpeg" or "webp")) return new("invalid", null, 0);
            ILocator? target = null;
            if (request.Target is not null)
            {
                try { target = await ResolveTargetAsync(session, request.Target, false, ct); }
                catch (BrowserTargetException ex) { return new(ex.Code, null, 0); }
                catch (BrowserTargetDeniedException) { return new("target_denied", null, 0); }
            }
            var secrets = await CollectSecretsAsync(session, ct);
            if (!ReferenceEquals(page, session.Page) || generation != session.Generation || !IsAllowed(session, page.Url))
                return new("target_denied", null, 0);
            var redactions = 0;
            var width = session.Page.ViewportSize?.Width ?? BrowserToolLimits.MaxCaptureWidth;
            var height = session.Page.ViewportSize?.Height ?? BrowserToolLimits.MaxCaptureHeight;
            byte[] png;
            var maskedFrames = new List<IFrame>();
            double maskingMs = 0, encodingMs = 0;
            try
            {
                foreach (var frame in session.Page.Frames)
                {
                    if (frame != session.Page.MainFrame)
                    {
                        // SDK locator masks pierce open shadow roots, but cannot reach closed roots.
                        // Never publish child-frame pixels when their whole-frame mask is unreachable.
                        await using var element = await frame.FrameElementAsync().WaitAsync(ct);
                        var maskable = await element.EvaluateAsync<bool>("""
                            element => {
                              if (!['iframe', 'frame'].includes(element.localName)) return false;
                              for (let root = element.getRootNode(); root instanceof ShadowRoot; root = root.host.getRootNode())
                                if (root.mode !== 'open') return false;
                              return true;
                            }
                            """).WaitAsync(ct);
                        if (!maskable) return new("target_denied", null, 0);
                    }
                    // Mask whole child frames: screenshots cannot prove that cross-origin pixels contain no secrets.
                    if (frame != session.Page.MainFrame && !Allows(session, frame.Url, true)) continue;
                    // Child frames are always masked as complete regions, including same-origin frames.
                    // A parent's unmasked exception never extends into another browsing context.
                    if (unmasked && frame == session.Page.MainFrame) continue;
                    maskedFrames.Add(frame);
                    redactions += await frame.EvaluateAsync<int>(
                """
                policy => {
                  const values = policy.values;
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
                  if (!policy.trustedGraphics)
                    for (const element of document.querySelectorAll('canvas, svg')) mask(element.getBoundingClientRect());
                  for (const element of document.querySelectorAll('*')) {
                    for (const pseudo of ['::before', '::after']) {
                      const content = getComputedStyle(element, pseudo).content || '';
                      if (matches(content)) mask(element.getBoundingClientRect());
                    }
                  }
                  return count;
                }
                """, new { values = secrets, trustedGraphics = privacy.TrustsGraphics(frame.Url) }).WaitAsync(ct);
                    redactions += await frame.EvaluateAsync<int>(MaskSensitiveScript).WaitAsync(ct);
                    // Hide carets using a Core-owned removable stylesheet. SDK caret hiding
                    // temporarily changes input styles, which would invalidate our own evidence.
                    await frame.EvaluateAsync("""
                        () => { const style = document.createElement('style');
                          style.setAttribute('data-agent-mask', '1');
                          style.textContent = '* { caret-color: transparent !important; }';
                          document.documentElement.appendChild(style); }
                        """).WaitAsync(ct);
                }
                var frameMasks = session.Page.Locator("iframe, frame");
                if (request.FullPage)
                {
                    var dimensions = await session.Page.EvaluateAsync<int[]>("() => [document.documentElement.scrollWidth, document.documentElement.scrollHeight]").WaitAsync(ct);
                    width = dimensions[0]; height = dimensions[1];
                    if (width > 1920 || height > 12000) return new("capture_too_large", null, redactions, width, height);
                }
                maskingMs = stage.Elapsed.TotalMilliseconds;
                stage.Restart();
                if (target is null)
                    png = await session.Page.ScreenshotAsync(new PageScreenshotOptions
                    {
                        Type = ScreenshotType.Png, FullPage = request.FullPage, Scale = ScreenshotScale.Css,
                        Caret = ScreenshotCaret.Initial, Mask = [frameMasks], Timeout = TimeoutMs()
                    }).WaitAsync(ct);
                else
                    png = await target.ScreenshotAsync(new LocatorScreenshotOptions
                    { Type = ScreenshotType.Png, Scale = ScreenshotScale.Css, Caret = ScreenshotCaret.Initial,
                      Mask = unmasked ? [frameMasks] : [session.Page.Locator("input[type=password], input[type=hidden]"), frameMasks], Timeout = TimeoutMs() }).WaitAsync(ct);
                if (format != "png")
                {
                    using var image = SKImage.FromEncodedData(png);
                    using var output = image.Encode(format == "jpeg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Webp, 85);
                    png = output.ToArray();
                }
                encodingMs = stage.Elapsed.TotalMilliseconds;
            }
            finally
            {
                foreach (var frame in maskedFrames)
                    try { await frame.EvaluateAsync(ClearMaskScript).WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { await CloseQuietlyAsync(session.Context); ForgetClosed(session); }
            }

            _logger.LogInformation("browser.screenshot.stages semantic_ms={SemanticMs} masking_ms={MaskingMs} encoding_ms={EncodingMs}",
                semanticMs, maskingMs, encodingMs);
            _logger.LogInformation(
                "browser.screenshot redactions={RedactionCount} bytes={ByteSize} width={Width} height={Height}",
                redactions,
                png.Length,
                width,
                height);
            if (png.Length > _policy.Limits.CaptureBytes)
            {
                return new BrowserScreenshotResult("capture_too_large", null, redactions, width, height);
            }

            using (var decoded = SKImage.FromEncodedData(png))
            {
                width = decoded.Width;
                height = decoded.Height;
            }
            var currentState = await ReadVisualStateAsync(page, ct);
            ct.ThrowIfCancellationRequested();
            var consistent = !page.IsClosed
                && ReferenceEquals(page, session.Page) && generation == session.Generation
                && visualState is not null && visualState == currentState;
            observation = observation with { Settled = settled && consistent };
            var coordinateEvidence = !request.FullPage && request.Target is null && settled && consistent;
            if (coordinateEvidence)
            {
                session.VisualSnapshotId = observation.SnapshotId;
                session.VisualState = currentState;
                session.VisualPage = page;
                session.VisualSessionId = request.SessionId;
                session.VisualPageGeneration = generation;
            }
            return new BrowserScreenshotResult(null, png, redactions, width, height, "image/" + format,
                observation, coordinateEvidence);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !CaptureLifetime.IsCancellationRequested)
        {
            return new("timeout", null, 0);
        }
        catch (OperationCanceledException) { throw; }
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

    private bool DeclaredOverDownloadCap(IDictionary<string, string> headers)
    {
        foreach (var pair in headers)
        {
            if (pair.Key.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(pair.Value, out var length))
            {
                return length > _policy.Limits.DownloadBytes;
            }
        }

        return false;
    }

    private async Task StreamDocumentAsync(SessionBrowser session, IRoute route, string url, int call, bool documentNavigation)
    {
        // A child frame may load after its parent operation has returned. Its bounded
        // page read must not inherit the expired operation token. Root navigation and
        // its attachment retain the initiating operation's cancellation boundary.
        var operationOwned = documentNavigation && session.OperationActive && route.Request.Frame == session.Page.MainFrame;
        using var timeout = operationOwned
            ? CancellationTokenSource.CreateLinkedTokenSource(session.OperationToken)
            : new CancellationTokenSource();
        timeout.CancelAfter(OperationTimeout);
        var ct = timeout.Token;
        session.InFlightRoute = route;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Request.Method), url);
            if (route.Request.PostDataBuffer is { } body)
            {
                if (body.Length > BrowserToolLimits.MaxDocumentBytes) { await AbortQuietlyAsync(route); return; }
                request.Content = new ByteArrayContent(body);
            }
            foreach (var pair in await route.Request.AllHeadersAsync())
                if (pair.Key is not ("host" or "content-length" or "transfer-encoding"))
                    if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value)) request.Content?.Headers.TryAddWithoutValidation(pair.Key, pair.Value);

            using var client = new HttpClient(new SocketsHttpHandler
            {
                Proxy = new WebProxy(ProxyFor(Guid.Empty, session.AgentInstanceId).Server), UseProxy = true,
                AllowAutoRedirect = false, UseCookies = false, MaxResponseDrainSize = 0,
                AutomaticDecompression = DecompressionMethods.None
            }) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var headers = HeaderMap(response);
            if (IsRedirect((int)response.StatusCode) && !RedirectStaysAllowed(session, url, headers, documentNavigation))
            {
                if (documentNavigation) session.DeniedNavigation = true;
                await AbortQuietlyAsync(route);
                return;
            }
            if (documentNavigation && TryAttachmentFileName(headers, out var name))
            {
                var download = await ReadBoundedAttachmentAsync(session, response, name, call, ct);
                ct.ThrowIfCancellationRequested();
                lock (session.PopupGate)
                {
                    if (!session.OperationActive || session.OperationCall != call || IsCancelled(session, call)) return;
                    if (session.StagedDownloads.Count < _policy.Limits.DownloadsPerScope) session.StagedDownloads.Add(download);
                }
                await route.FulfillAsync(new() { Status = 204, Body = "" });
                return;
            }
            // Documents also have a memory ceiling. Encoded bytes and response headers remain intact.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, ct);
                if (read == 0) break;
                if (buffer.Length + read > BrowserToolLimits.MaxDocumentBytes) { await AbortQuietlyAsync(route); return; }
                buffer.Write(chunk, 0, read);
            }
            ct.ThrowIfCancellationRequested();
            if (operationOwned && IsCancelled(session, call)) { await AbortQuietlyAsync(route); return; }
            await route.FulfillAsync(new() { Status = (int)response.StatusCode, Headers = headers, BodyBytes = buffer.ToArray() });
        }
        catch (OperationCanceledException)
        {
            if (session.OperationActive && session.OperationCall == call && !IsCancelled(session, call)) session.TimedOut = true;
            await AbortQuietlyAsync(route);
        }
        catch (HttpRequestException) { await AbortQuietlyAsync(route); }
        finally { if (ReferenceEquals(session.InFlightRoute, route)) session.InFlightRoute = null; }
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
        var limit = _policy.Limits.DownloadBytes;
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

    private static Dictionary<string, string> HeaderMap(HttpResponseMessage response)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in response.Headers)
        {
            map[pair.Key] = string.Join(pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", pair.Value);
        }

        foreach (var pair in response.Content.Headers)
        {
            map[pair.Key] = string.Join(pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", pair.Value);
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

    private async Task<BrowserDownload> ReadDownloadAsync(IDownload download, CancellationToken cancellationToken)
    {
        var fileName = BrowserDownloadPolicy.SanitizeFileName(download.SuggestedFilename);
        try
        {
            using var cancel = cancellationToken.Register(() => _ = CancelDownloadQuietlyAsync(download));
            await using var stream = await download.CreateReadStreamAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            var limit = _policy.Limits.DownloadBytes;
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
        catch (OperationCanceledException) { await CancelDownloadQuietlyAsync(download); throw; }
        catch (PlaywrightException)
        {
            return new BrowserDownload("download_rejected", fileName, null, null);
        }
        finally
        {
            try { await download.DeleteAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
        }
    }

    private static async Task CancelDownloadQuietlyAsync(IDownload download)
    {
        try { await download.CancelAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
        try { await download.DeleteAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException) { }
    }

    private sealed record BrowserScreenshotRequest(Guid SessionId, string Format = "png", bool FullPage = false, BrowserTarget? Target = null);
    private sealed record BrowserScreenshotResult(string? ErrorCode, byte[]? Png, int RedactionCount,
        int Width = 0, int Height = 0, string? ContentType = null,
        BrowserSnapshot? Observation = null, bool CoordinateEvidence = false);

    // Reuse the settle observer for freshness. No page content or protected values leave this check.
    private static Task<string?> ReadVisualStateAsync(IPage page, CancellationToken ct) =>
        page.EvaluateAsync<string?>("""
            () => window.__acSettle && window.__acSettle.inflight === 0 && document.fonts.status === 'loaded'
              && !document.getAnimations().some(animation => animation.playState === 'running')
              ? JSON.stringify([performance.timeOrigin, window.__acSettle.visualGeneration,
                innerWidth, innerHeight, scrollX, scrollY]) : null
            """).WaitAsync(ct);
}
