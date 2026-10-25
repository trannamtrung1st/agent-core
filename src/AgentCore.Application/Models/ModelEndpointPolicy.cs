using System.Net;
using AgentCore.Application.Ports;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Models;

// Host-owned allowances; tenant administrators cannot turn off network policy.
public sealed class ModelEndpointPolicy(IReadOnlyList<string>? allowedLocalOrigins = null)
{
    private readonly HashSet<string> localOrigins = (allowedLocalOrigins ?? []).Select(Origin).ToHashSet(StringComparer.Ordinal);
    public static string DefaultEndpoint(ModelProviderKind kind) => kind switch
    {
        ModelProviderKind.OpenAI => "https://api.openai.com/v1/",
        ModelProviderKind.Anthropic => "https://api.anthropic.com/v1/",
        ModelProviderKind.Google => "https://generativelanguage.googleapis.com/v1beta/",
        ModelProviderKind.OpenRouter => "https://openrouter.ai/api/v1/",
        _ => throw new ArgumentException("Custom providers require an explicit endpoint.")
    };
    public string Validate(ModelProviderKind kind, string? endpoint, bool noCredential)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("Unknown provider kind.");
        if (kind != ModelProviderKind.OpenAICompatible)
        {
            var expected = DefaultEndpoint(kind);
            if (noCredential || endpoint is not null && endpoint.TrimEnd('/') + "/" != expected)
                throw new ArgumentException("Native providers require their built-in endpoint and a protected credential.");
            return expected;
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || endpoint.Length > 2048
            || endpoint.Any(char.IsControl) || uri.Host.EndsWith('.') || uri.AbsolutePath.Contains("%", StringComparison.Ordinal))
            throw new ArgumentException("Use an absolute HTTP(S) endpoint without credentials, query, fragment or escaped path.");
        var local = IsLocalAllowed(uri);
        if (!local && (uri.Scheme != "https" || !PublicNetworkPolicy.IsAllowedHostName(uri.Host) || noCredential))
            throw new ArgumentException("Local/unauthenticated endpoints require an explicit host origin allowance; public endpoints require HTTPS and a credential.");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    public bool IsLocalAllowed(Uri uri) => localOrigins.Contains(uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant())
        && !IsMetadata(uri.Host);
    // The dispatch handler applies this to the actual resolved socket address, preventing DNS rebinding.
    public bool AllowsAddress(Uri uri, IPAddress address) => PublicNetworkPolicy.IsAllowed(address)
        || IsLocalAllowed(uri) && IsPrivateAddress(address) && !IsMetadata(address.ToString());
    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
            : bytes[0] is 0xfc or 0xfd;
    }
    private static bool IsMetadata(string host) => host.Trim('[', ']').Equals("169.254.169.254", StringComparison.OrdinalIgnoreCase)
        || host.Equals("metadata", StringComparison.OrdinalIgnoreCase) || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host, out var ip) && (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() == "169.254.169.254" : ip.ToString() == "fd00:ec2::254");
    private static string Origin(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || IsMetadata(uri.Host))
            throw new ArgumentException("Model local allowances must be exact origins and cannot allow metadata services.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
