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
    public static BrowserTargetDecision EvaluateDestination(
        string? url,
        IReadOnlyList<string>? targetOrigins,
        BrowserPolicyMode policyMode = BrowserPolicyMode.Restricted)
    {
        if (!TryHttpTarget(url, out var uri, out var rejected))
        {
            return rejected;
        }

        if (IsMetadataTarget(uri)) return BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
        if (policyMode == BrowserPolicyMode.OpenWeb) return BrowserTargetDecision.Allow;

        if (!IsListedOrigin(uri, targetOrigins))
        {
            return BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
        }

        return BrowserTargetDecision.Allow;
    }

    public static BrowserTargetDecision EvaluatePopup(
        string? url,
        IReadOnlyList<string>? targetOrigins,
        BrowserPolicyMode policyMode = BrowserPolicyMode.Restricted)
    {
        return EvaluateDestination(url, targetOrigins, policyMode);
    }

    public static BrowserTargetDecision EvaluateResource(
        string? url,
        IReadOnlyList<string>? navigationOrigins,
        IReadOnlyList<string>? resourceOrigins,
        BrowserPolicyMode policyMode = BrowserPolicyMode.Restricted)
    {
        if (policyMode == BrowserPolicyMode.OpenWeb)
        {
            return EvaluateDestination(url, navigationOrigins, policyMode);
        }

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
        IReadOnlyList<string>? interactionOrigins,
        BrowserPolicyMode policyMode = BrowserPolicyMode.Restricted)
    {
        if (interactionMode != BrowserInteractionMode.InteractiveDemo)
        {
            return BrowserTargetDecision.Deny("forbidden", "Browser actions are not allowed in this interaction mode.");
        }

        var page = policyMode == BrowserPolicyMode.OpenWeb
            ? EvaluateDestination(currentPageUrl, interactionOrigins, policyMode)
            : EvaluateDestination(currentPageUrl, interactionOrigins);
        if (!page.Allowed)
        {
            return BrowserTargetDecision.Deny("forbidden", "Browser actions are limited to a trusted interaction origin.");
        }

        return BrowserTargetDecision.Allow;
    }

    private static bool TryHttpTarget(string? url, out Uri uri, out BrowserTargetDecision rejected)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            rejected = BrowserTargetDecision.Deny("invalid", "url is required.");
            return false;
        }

        if (url.Length > BrowserToolLimits.MaxUrlLength)
        {
            rejected = BrowserTargetDecision.Deny("invalid", "url must be at most 2048 characters.");
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri!))
        {
            rejected = BrowserTargetDecision.Deny("invalid", "url must be an absolute http or https URL.");
            return false;
        }

        if (!IsHttp(uri) || !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.Contains('*', StringComparison.Ordinal))
        {
            rejected = BrowserTargetDecision.Deny("target_denied", "Browser target is not allowed.");
            return false;
        }

        rejected = default;
        return true;
    }

    private static bool IsMetadataTarget(Uri uri)
    {
        var host = NormalizeHost(uri.Host);
        if (host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return false;
        }

        return IsForbiddenAddress(address);
    }

    public static bool IsForbiddenAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 0 || bytes[0] >= 224
                || address.Equals(IPAddress.Parse("100.100.100.200"));
        }

        return address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv6LinkLocal
            || address.Equals(IPAddress.Parse("fd00:ec2::254"));
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
