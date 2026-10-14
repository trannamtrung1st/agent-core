using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Sandbox;

public sealed class OpenSandboxOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://127.0.0.1:18080";
    private string? _apiKey;
    public string? ApiKey { get => _apiKey ?? (ApiKeyFile == "/run/secrets/opensandbox_api_key" ? File.ReadAllText(ApiKeyFile).Trim() : null); set => _apiKey = value; }
    public string? ApiKeyFile { get; set; }
    public bool TrustLocalComposeEndpoint { get; set; }
    public string? WorkbenchImage { get; set; }
    public bool AllowLocalImageIds { get; set; }
    public string[] AllowedImagePrefixes { get; set; } = ["docker.io/agent-core/", "ghcr.io/trannamtrung1st/"];
    public void ValidateProfile(SandboxProfile profile)
    {
        profile.Validate();
        if (!(AllowLocalImageIds && profile.ImageReference.StartsWith("sha256:", StringComparison.Ordinal))
            && !AllowedImagePrefixes.Any(p => profile.ImageReference.StartsWith(p, StringComparison.Ordinal)))
            throw new ArgumentException("Sandbox image is outside the host registry allowlist.");
    }
    internal Uri Origin()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.Scheme == "http" && !uri.IsLoopback && !(TrustLocalComposeEndpoint && uri.Host == "opensandbox" && uri.Port == 8080) || string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("OpenSandbox requires a private authenticated origin. Configure loopback HTTP or HTTPS.");
        return uri;
    }
}
