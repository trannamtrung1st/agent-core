using System.Net;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public readonly record struct BrowserTargetDecision(bool Allowed, string? Code, string? Message)
{
    public static BrowserTargetDecision Allow { get; } = new(true, null, null);

    public static BrowserTargetDecision Deny(string code, string message) => new(false, code, message);
}

public static class BrowserTargetPolicy
{
    public static BrowserTargetDecision EvaluateDestination(string? url, IReadOnlyList<string>? targetOrigins)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return BrowserTargetDecision.Deny("invalid", "url is required.");
        }

        if (url.Length > BrowserToolLimits.MaxUrlLength)
        {
            return BrowserTargetDecision.Deny("invalid", "url must be at most 2048 characters.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return BrowserTargetDecision.Deny("invalid", "url must be an absolute http or https URL.");
        }

        if (!IsHttp(uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
        }

        if (uri.Host.Contains('*', StringComparison.Ordinal))
        {
            return BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
        }

        if (!IsListedOrigin(uri, targetOrigins))
        {
            return BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
        }

        return BrowserTargetDecision.Allow;
    }

    public static BrowserTargetDecision EvaluatePopup(string? url, IReadOnlyList<string>? targetOrigins)
    {
        var destination = EvaluateDestination(url, targetOrigins);
        if (!destination.Allowed)
        {
            return destination;
        }

        return BrowserTargetDecision.Deny("unsupported_operation", "Popups are not a browser result surface.");
    }

    public static BrowserTargetDecision EvaluateResource(
        string? url,
        IReadOnlyList<string>? navigationOrigins,
        IReadOnlyList<string>? resourceOrigins)
    {
        var navigation = EvaluateDestination(url, navigationOrigins);
        if (navigation.Allowed)
        {
            return navigation;
        }

        var resource = EvaluateDestination(url, resourceOrigins);
        return resource.Allowed
            ? BrowserTargetDecision.Allow
            : BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
    }

    public static BrowserTargetDecision EvaluateAct(
        BrowserInteractionMode interactionMode,
        string? currentPageUrl,
        IReadOnlyList<string>? interactionOrigins)
    {
        if (interactionMode != BrowserInteractionMode.InteractiveDemo)
        {
            return BrowserTargetDecision.Deny("forbidden", "Browser actions are not allowed in this interaction mode.");
        }

        var page = EvaluateDestination(currentPageUrl, interactionOrigins);
        if (!page.Allowed)
        {
            return BrowserTargetDecision.Deny("forbidden", "Browser actions are limited to a trusted interaction origin.");
        }

        return BrowserTargetDecision.Allow;
    }

    public static bool IsLoopback(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(NormalizeHost(uri.Host), out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsListedOrigin(Uri uri, IReadOnlyList<string>? targetOrigins)
    {
        if (targetOrigins is null)
        {
            return false;
        }

        foreach (var entry in targetOrigins)
        {
            if (string.IsNullOrWhiteSpace(entry)
                || entry.Contains('*', StringComparison.Ordinal)
                || !Uri.TryCreate(entry, UriKind.Absolute, out var allowed)
                || !IsHttp(allowed)
                || !string.IsNullOrEmpty(allowed.UserInfo))
            {
                continue;
            }

            if (string.Equals(uri.Scheme, allowed.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(NormalizeHost(uri.Host), NormalizeHost(allowed.Host), StringComparison.OrdinalIgnoreCase)
                && uri.Port == allowed.Port)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string host)
    {
        var trimmed = host.Trim().TrimStart('[').TrimEnd(']');
        return IPAddress.TryParse(trimmed, out var address) ? address.ToString() : trimmed;
    }
}
