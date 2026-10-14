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
    public string[] InsecureRegistryHosts { get; set; } = [];
    public string[] AllowedRegistryTokenOrigins { get; set; } = [];
    public bool AllowLocalImageIds { get; set; }
    public string[] AllowedImagePrefixes { get; set; } = ["docker.io/agent-core/", "ghcr.io/trannamtrung1st/"];
    public void ValidateProfile(SandboxProfile profile)
    {
        profile.Validate();
        var source = OciImageReference.Parse(profile.ImageReference);
        if (source.IsLocal ? !AllowLocalImageIds : !AllowedImagePrefixes.Any(source.IsWithin))
            throw new ArgumentException("Sandbox image is outside the host registry allowlist.");
    }
    public void ValidatePolicy()
    {
        foreach (var prefix in AllowedImagePrefixes) _ = OciImageReference.ParseScope(prefix);
        foreach (var host in InsecureRegistryHosts)
            if (!Uri.TryCreate("http://" + OciImageReference.CanonicalRegistry(host), UriKind.Absolute, out var uri) || !uri.IsLoopback)
                throw new ArgumentException("Insecure registry exceptions are restricted to exact loopback hosts and ports.");
        foreach (var origin in AllowedRegistryTokenOrigins)
            if (!Uri.TryCreate(origin,UriKind.Absolute,out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || origin != uri.GetLeftPart(UriPartial.Authority))
                throw new ArgumentException("Registry token-service origins must be exact HTTPS origins.");
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
