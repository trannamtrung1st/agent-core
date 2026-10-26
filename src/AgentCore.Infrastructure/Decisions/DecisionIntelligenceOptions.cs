namespace AgentCore.Infrastructure.Decisions;

public enum DecisionBackendKind { Disabled, LocalKev, AuthorizedLlm, Synthetic }

public sealed class DecisionIntelligenceOptions
{
    public const string SectionName = "DecisionIntelligence";
    public const string KevSource = "6b719c3c3f367295f6ef336f4f751cf5ff970abc";
    public const string KevModel = "jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52";
    public bool Enabled { get; set; }
    public DecisionBackendKind Backend { get; set; } = DecisionBackendKind.LocalKev;
    public string Endpoint { get; set; } = "http://127.0.0.1:8009";
    public string? ApiKey { get; set; }
    public string ModelId { get; set; } = KevModel;
    public int RequestTimeoutMs { get; set; } = 500;
    public int MaxConcurrentRequests { get; set; } = 4;
    public int MaxStateCharacters { get; set; } = 8000;
    public bool ShadowMode { get; set; } = true;

    public void Validate(bool synthetic)
    {
        if (!Enum.IsDefined(Backend) || RequestTimeoutMs is < 50 or > 10000 || MaxConcurrentRequests is < 1 or > 16
            || MaxStateCharacters is < 512 or > 16000 || ModelId != KevModel
            || Backend == DecisionBackendKind.Synthetic && !synthetic)
            throw new InvalidOperationException("Invalid DecisionIntelligence configuration. Use bounded canonical options and the pinned Kev-4B checkpoint; Synthetic is test-profile only.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http"
            || endpoint.Port is < 1 or > 65535 || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/" || !AllowedHost(endpoint.Host))
            throw new InvalidOperationException("DecisionIntelligence endpoint must be HTTP loopback, the internal decision service, or Docker's local host bridge, without path, user info, query or fragment.");
        if (ApiKey is not null && (ApiKey is not { Length: >= 16 and <= 256 } || ApiKey.Any(c => c is < '!' or > '~')))
            throw new InvalidOperationException("A configured LocalKev key must be private and DecisionIntelligence:ApiKey containing 16–256 printable non-space ASCII characters.");
    }

    internal static bool AllowedHost(string host) => host is "127.0.0.1" or "[::1]" or "::1" or "decision" or "host.docker.internal";
}
