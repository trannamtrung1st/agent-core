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
    private async Task RouteAsync(SessionBrowser session, IRoute route)
    {
        var url = route.Request.Url;
        IPage? page = null;
        try
        {
            page = route.Request.Frame?.Page;
        }
        catch (Exception)
        {
            page = null;
        }

        try
        {
            var lease = LeaseOrigins(session);
            if (!ReferenceEquals(page, session.Page))
            {
                if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                {
                    RememberPage(session, page);
                    await route.ContinueAsync().ConfigureAwait(false);
                    return;
                }

                string? popupDenial = null;
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var popup = BrowserTargetPolicy.EvaluatePopup(
                        url,
                        lease ?? _policy.NavigationOrigins,
                        lease is null ? _policy.PolicyMode : BrowserPolicyMode.Restricted);
                    if (popup.Allowed && Allows(session, url, true))
                    {
                        RememberPage(session, page);
                        await route.ContinueAsync().ConfigureAwait(false);
                        return;
                    }

                    if (string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase))
                    {
                        popupDenial = popup.Code ?? "target_denied";
                    }
                }

                var document = string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase);
                TaskCompletionSource? cleanup = document ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                // Publish the cleanup receipt before its denial. A still-about:blank popup
                // otherwise looks allowed while the route callback is aborting/closing it.
                if (cleanup is not null) lock (session.PopupGate) session.PopupCloses.Add(cleanup.Task);
                if (popupDenial is not null) session.PopupCode ??= popupDenial;
                try
                {
                    // Initial popup requests can precede the SDK's Page event. Register
                    // that event before aborting so cleanup includes the resulting blank page.
                    var born = page is null && document
                        ? session.Context.WaitForPageAsync(new() { Timeout = TimeoutMs(), Predicate = candidate => !session.CallPages.Contains(candidate) })
                        : null;
                    await route.AbortAsync().ConfigureAwait(false);
                    if (born is not null) page = await born.ConfigureAwait(false);
                    if (page is not null && document)
                    {
                        ForgetPage(session, page);
                        await (DeniedPopupCloseProbe is { } closePopup ? closePopup(page) : CloseQuietlyAsync(page)).ConfigureAwait(false);
                    }
                }
                finally { cleanup?.TrySetResult(); }

                return;
            }

            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                await route.ContinueAsync().ConfigureAwait(false);
                return;
            }

            if (IsCancelled(session, session.OperationCall))
            {
                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            var documentNavigation = string.Equals(route.Request.ResourceType, "document", StringComparison.OrdinalIgnoreCase);
            if (!Allows(session, url, documentNavigation))
            {
                if (documentNavigation)
                {
                    session.DeniedNavigation = true;
                }

                await route.AbortAsync().ConfigureAwait(false);
                return;
            }

            BrowserRouteRule? rule;
            lock (session.PopupGate) rule = session.Rules.LastOrDefault(r => string.Equals(r.Url, url, StringComparison.Ordinal));
            if (rule is not null)
            {
                if (rule.Action == "abort") await route.AbortAsync().ConfigureAwait(false);
                else await route.FulfillAsync(new RouteFulfillOptions { Status = rule.Status, ContentType = "text/plain", Body = rule.Body }).ConfigureAwait(false);
                return;
            }

            if (lease is null && _policy.PolicyMode == BrowserPolicyMode.OpenWeb)
            {
                if (IsCancelled(session, session.OperationCall))
                {
                    await route.AbortAsync().ConfigureAwait(false);
                    return;
                }

                await route.ContinueAsync().ConfigureAwait(false);
                return;
            }

            await FulfillWithoutLeavingPolicyAsync(session, route, url, documentNavigation).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            await AbortQuietlyAsync(route).ConfigureAwait(false);
        }
    }

    private Task FulfillWithoutLeavingPolicyAsync(SessionBrowser session, IRoute route, string url, bool documentNavigation) =>
        StreamDocumentAsync(session, route, url, session.OperationCall, documentNavigation);

    private bool RedirectStaysAllowed(SessionBrowser session, string requestUrl, IDictionary<string, string> headers, bool documentNavigation)
    {
        string? location = null;
        foreach (var header in headers)
        {
            if (header.Key.Equals("location", StringComparison.OrdinalIgnoreCase))
            {
                location = header.Value;
                break;
            }
        }

        return !string.IsNullOrWhiteSpace(location)
            && Uri.TryCreate(requestUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, location, out var resolved)
            && Allows(session, resolved.AbsoluteUri, documentNavigation);
    }

    private static bool IsRedirect(int status) => status is 301 or 302 or 303 or 307 or 308;

    private static async Task AbortQuietlyAsync(IRoute route)
    {
        try
        {
            await route.AbortAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
        }
    }

    private void OnContextPage(SessionBrowser session, IPage opened)
    {
        if (session.AcceptingMainPage || ReferenceEquals(opened, session.Page))
        {
            return;
        }

        RememberPage(session, opened);
        if (LeaseOrigins(session) is null && _policy.PolicyMode == BrowserPolicyMode.OpenWeb)
        {
            session.PendingOpenedPage = opened;
        }
    }

    private async Task AdoptOpenWebPageAsync(SessionBrowser session, CancellationToken cancellationToken)
    {
        if (LeaseOrigins(session) is not null || _policy.PolicyMode != BrowserPolicyMode.OpenWeb)
        {
            return;
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pending = session.PendingOpenedPage;
            if (pending is null)
            {
                RememberAllowedUrl(session);
                return;
            }

            if (pending.IsClosed)
            {
                ClearPending(session, pending);
                RememberAllowedUrl(session);
                return;
            }

            var url = pending.Url;
            if (IsAllowed(url))
            {
                pending.SetDefaultTimeout(TimeoutMs());
                pending.SetDefaultNavigationTimeout(TimeoutMs());
                session.Page = pending;
                ClearPending(session, pending);
                AdvanceGeneration(session);
                session.LastAllowedUrl = url;
                if (session.PendingOpenedPage is null)
                {
                    return;
                }

                continue;
            }

            if (IsConcreteDeniedUrl(url))
            {
                ClearPending(session, pending);
                await CloseQuietlyAsync(pending).ConfigureAwait(false);
                if (session.PendingOpenedPage is null)
                {
                    return;
                }

                continue;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ClearPending(SessionBrowser session, IPage pending)
    {
        if (ReferenceEquals(session.PendingOpenedPage, pending))
        {
            session.PendingOpenedPage = null;
        }
    }

    private void RememberAllowedUrl(SessionBrowser session)
    {
        if (IsAllowed(session, session.Page.Url))
        {
            session.LastAllowedUrl = session.Page.Url;
        }
    }

    private bool IsConcreteDeniedUrl(string url) =>
        !string.IsNullOrWhiteSpace(url)
        && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
        && !IsAllowed(url);

    private async Task SettlePopupsAsync(SessionBrowser session)
    {
        if (_policy.PolicyMode == BrowserPolicyMode.OpenWeb)
        {
            return;
        }

        try
        {
            // A native popup can be exposed as about:blank before its first routed navigation.
            // Await that native transition before deciding it is a permitted blank tab.
            var starting = session.Context.Pages.Where(page => !ReferenceEquals(page, session.Page)
                && !session.CallPages.Contains(page) && page.Url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)).ToArray();
            await Task.WhenAll(starting.Select(async page =>
            {
                try { await page.WaitForURLAsync(url => !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase),
                    new() { Timeout = 400, WaitUntil = WaitUntilState.DOMContentLoaded }); }
                catch (PlaywrightException) { } // Denial closes/aborts the provisional page; cleanup below owns that result.
            }));
            for (var attempt = 0; attempt < 8; attempt++)
            {
                Task[] pending;
                lock (session.PopupGate)
                {
                    pending = session.PopupCloses.ToArray();
                    session.PopupCloses.Clear();
                }

                if (pending.Length > 0)
                {
                    PopupCleanupWaitProbe?.Invoke();
                    await Task.WhenAll(pending).ConfigureAwait(false);
                }
                await ClosePopupsAsync(session).ConfigureAwait(false);

                var extras = session.Context.Pages.Count(page => !ReferenceEquals(page, session.Page) && !KeepPopup(session, page));
                var stillClosing = false;
                lock (session.PopupGate)
                {
                    stillClosing = session.PopupCloses.Count > 0;
                }

                if (extras == 0 && !stillClosing)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
        }
    }

    private async Task ClosePopupsAsync(SessionBrowser session)
    {
        foreach (var page in session.Context.Pages.ToArray())
        {
            if (ReferenceEquals(page, session.Page) || KeepPopup(session, page))
            {
                continue;
            }

            ForgetPage(session, page);
            PopupCleanupWaitProbe?.Invoke();
            await (DeniedPopupCloseProbe is { } closePopup ? closePopup(page) : CloseQuietlyAsync(page)).ConfigureAwait(false);
        }
    }

    private bool KeepPopup(SessionBrowser session, IPage page)
    {
        string url;
        try
        {
            if (page.IsClosed)
            {
                return false;
            }

            url = page.Url;
        }
        catch (PlaywrightException)
        {
            return false;
        }

        // Initial popup navigation can be routed before Playwright exposes its Frame/Page.
        // After denial, discard only newly opened blank pages from this failed call;
        // previously owned blank tabs and successfully allowed destinations are retained.
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return session.PopupCode is null || session.CallPages.Contains(page);
        return IsAllowed(session, url);
    }

    private bool Allows(SessionBrowser session, string url, bool documentNavigation)
    {
        var lease = LeaseOrigins(session);
        if (lease is not null)
        {
            var hostAllowed = documentNavigation
                ? BrowserTargetPolicy.EvaluateDestination(url, _policy.NavigationOrigins, _policy.PolicyMode).Allowed
                : BrowserTargetPolicy.EvaluateResource(url, _policy.NavigationOrigins, _policy.EffectiveResourceOrigins, _policy.PolicyMode).Allowed;
            if (!hostAllowed) return false;
            return documentNavigation
                ? BrowserTargetPolicy.EvaluateDestination(url, lease, BrowserPolicyMode.Restricted).Allowed
                : BrowserTargetPolicy.EvaluateResource(url, lease, lease, BrowserPolicyMode.Restricted).Allowed;
        }

        return documentNavigation
            ? BrowserTargetPolicy.EvaluateDestination(url, _policy.NavigationOrigins, _policy.PolicyMode).Allowed
            : BrowserTargetPolicy.EvaluateResource(
                url,
                _policy.NavigationOrigins,
                _policy.EffectiveResourceOrigins,
                _policy.PolicyMode).Allowed;
    }

    private bool CanKeepInterruptedNavigation(SessionBrowser session, PlaywrightException exception)
    {
        if (!BrowserFailureClassifier.IsInterruptedNavigation(exception.Message))
        {
            return false;
        }

        var url = session.Page.Url;
        return Uri.TryCreate(url, UriKind.Absolute, out var landed)
            && landed.Scheme is "http" or "https"
            && IsAllowed(session, url);
    }

    private bool IsAllowed(string url) =>
        BrowserTargetPolicy.EvaluateDestination(url, _policy.NavigationOrigins, _policy.PolicyMode).Allowed;

    private bool IsAllowed(SessionBrowser session, string url)
    {
        var lease = LeaseOrigins(session);
        return lease is null
            ? IsAllowed(url)
            : IsAllowed(url) && BrowserTargetPolicy.EvaluateDestination(url, lease, BrowserPolicyMode.Restricted).Allowed;
    }

    private void RememberOpenPages(SessionBrowser session)
    {
        foreach (var page in session.Context.Pages.ToArray())
        {
            RememberPage(session, page);
        }
    }

    private static List<PageBinding> OpenPages(SessionBrowser session)
    {
        lock (session.PopupGate)
        {
            session.Pages.RemoveAll(item => PageClosed(item.Page));
            return session.Pages.ToList();
        }
    }

    private async Task<IReadOnlyList<BrowserPageInfo>> DescribePagesAsync(SessionBrowser session, CancellationToken ct)
    {
        var active = session.Page;
        var pages = OpenPages(session);
        var secrets = new List<string>();
        foreach (var binding in pages.Where(p => IsAllowed(session, SafePageUrl(p.Page))))
            foreach (var value in await CollectSecretsAsync(session, ct, binding.Page)) AddSecret(secrets, value);
        return pages
            .Select(item => new BrowserPageInfo(item.Id, IsAllowed(session, SafePageUrl(item.Page))
                ? SafeBrowserUrl(SafePageUrl(item.Page), secrets, session.ProtectedValues)
                : SafeNetworkUrl(SafePageUrl(item.Page)), ReferenceEquals(item.Page, active)))
            .ToArray();
    }

    private static PageBinding? FindPage(SessionBrowser session, string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        lock (session.PopupGate)
        {
            return session.Pages.FirstOrDefault(item => string.Equals(item.Id, pageId, StringComparison.Ordinal));
        }
    }

    private void RememberPage(SessionBrowser session, IPage? page)
    {
        if (page is null || PageClosed(page))
        {
            return;
        }

        var added = false;
        lock (session.PopupGate)
        {
            if (session.Pages.Any(item => ReferenceEquals(item.Page, page)))
            {
                return;
            }

            session.Pages.Add(new PageBinding(MintPageToken(), page));
            added = true;
        }

        if (added)
        {
            page.FrameNavigated += (_, frame) => { if (ReferenceEquals(session.Page, page)) { AdvanceGeneration(session); } };
            page.FrameDetached += (_, frame) => { if (ReferenceEquals(session.Page, page)) { AdvanceGeneration(session); } };
            page.Dialog += (_, dialog) => { session.Dialog = dialog; session.DialogSignal.TrySetResult(); };
            page.Console += (_, message) => { lock (session.PopupGate) { session.Console.Add(message.Type + ": " + (message.Text.Length > 1000 ? "[redacted oversized console message]" : message.Text)); if (session.Console.Count > 50) session.Console.RemoveAt(0); } };
            page.Request += (_, request) => { lock (session.PopupGate) { session.Network["req_" + Guid.NewGuid().ToString("N")] = request; if (session.Network.Count > 50) session.Network.Remove(session.Network.Keys.First()); } };
            page.Download += (_, download) =>
            {
                lock (session.PopupGate)
                {
                    if (session.OperationActive && session.PendingDownloads.Count < _policy.Limits.DownloadsPerScope && !session.OperationToken.IsCancellationRequested)
                        session.PendingDownloads.Add(download);
                    else _ = CancelDownloadQuietlyAsync(download);
                }
            };
        }
    }

    private static void ForgetPage(SessionBrowser session, IPage page)
    {
        lock (session.PopupGate)
        {
            session.Pages.RemoveAll(item => ReferenceEquals(item.Page, page));
            session.Media.Remove(page);
        }
    }

    private static bool PageClosed(IPage page)
    {
        try
        {
            return page.IsClosed;
        }
        catch (PlaywrightException)
        {
            return true;
        }
    }

    private static string SafePageUrl(IPage page)
    {
        try
        {
            return page.Url;
        }
        catch (PlaywrightException)
        {
            return string.Empty;
        }
    }

    private static string MintPageToken()
    {
        var encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "pg_" + encoded;
    }

    private sealed class PageBinding(string id, IPage page)
    {
        public string Id { get; } = id;

        public IPage Page { get; } = page;
    }
}
