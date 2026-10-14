using System.Net.Http;
using System.Net.Http.Headers;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.PublicWeb;

internal sealed partial class PublicWebFetcher
{
    private readonly int _maxDownloadBytes = maxDownloadBytes is >= 1 and <= 50 * 1024 * 1024
        ? maxDownloadBytes : throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
    private readonly int _downloadTimeoutSeconds = downloadTimeoutSeconds is >= 1 and <= 30
        ? downloadTimeoutSeconds : throw new ArgumentOutOfRangeException(nameof(downloadTimeoutSeconds));
    public bool SupportsDownloads => true;

    public async ValueTask<PublicWebDownloadResult> DownloadAsync(PublicWebDownloadRequest request, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_downloadTimeoutSeconds));
        var current = request.Url;
        try
        {
            for (var hop = 0; hop <= PublicWebLimits.MaxRedirects; hop++)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!PublicWebUrlPolicy.TryValidate(current, out var code, out var message)) return Failure(code!, message!);
                var response = await transport.SendAsync(new("GET", current, [new("Accept", "*/*"), new("Accept-Encoding", "identity")], [], _maxDownloadBytes), timeout.Token);
                if (response.StatusCode is >= 300 and <= 399 && response.RedirectLocation is not null)
                {
                    if (hop == PublicWebLimits.MaxRedirects) return Failure("too_many_redirects", "Redirect limit exceeded.");
                    current = response.RedirectLocation;
                    continue;
                }
                if (response.StatusCode != 200)
                    return Failure("http_error", $"HTTP {response.StatusCode}. Public downloads require a complete unauthenticated response; login, browser state and CAPTCHA access are unavailable.");
                if (response.Body.Length > _maxDownloadBytes) return Failure("body_too_large", "Download exceeded the configured byte limit.");
                if (!string.IsNullOrEmpty(response.ContentEncoding) && !response.ContentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                    return Failure("unsupported_content_encoding", "Server returned encoded content despite identity encoding; no file was committed.");
                var filename = ContentDispositionHeaderValue.TryParse(response.ContentDisposition, out var disposition)
                    ? disposition.FileNameStar ?? disposition.FileName?.Trim('"') : null;
                return new(current.AbsoluteUri, response.ContentType, filename, response.Body, null, null);
            }
            return Failure("too_many_redirects", "Redirect limit exceeded.");
        }
        catch (PublicWebFetchException ex)
        {
            HandledFailureLog.Warning(logger, ex, "web.download", ex.Code, diagnostics: diagnosticIds);
            return Failure(ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            HandledFailureLog.Warning(logger, ex, "web.download", "transport_error", diagnostics: diagnosticIds);
            return Failure("transport_error", "Public download failed; no file was committed.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            HandledFailureLog.Warning(logger, ex, "web.download", "timeout", diagnostics: diagnosticIds);
            return Failure("timeout", "Public download timed out; no file was committed.");
        }
        PublicWebDownloadResult Failure(string code, string message) => new(current.ToString(), null, null, [], code, message);
    }
}
