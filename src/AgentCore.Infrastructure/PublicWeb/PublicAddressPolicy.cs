using System.Net;
using System.Net.Sockets;

namespace AgentCore.Infrastructure.PublicWeb;

internal static class PublicAddressPolicy
{
    public static bool IsAllowed(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                198 when bytes[1] is 18 or 19 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6Multicast
                || address.IsIPv6SiteLocal)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            if (bytes[0] == 0xFC || bytes[0] == 0xFD)
            {
                return false;
            }

            if (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)
            {
                return false;
            }

            // NAT64 well-known prefix 64:ff9b::/96 — treat as non-public for credential-free fetch.
            if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xFF && bytes[3] == 0x9B)
            {
                return false;
            }

            // Allow only global unicast (2000::/3); deny unique local and other non-public IPv6.
            if ((bytes[0] & 0xE0) != 0x20)
            {
                return false;
            }

            return true;
        }

        return false;
    }

    public static bool IsAllowedHostName(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.');
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (normalized.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("metadata", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IPAddress.TryParse(normalized, out var literal))
        {
            return IsAllowed(literal);
        }

        return true;
    }
}
