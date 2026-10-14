using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Credentials;

/// <summary>Nonsecret host-side pull authority; never an agent credential binding.</summary>
public sealed record ContainerRegistryProfile(string Registry, string Username, IReadOnlyList<string> RepositoryPrefixes,
    DateTimeOffset? ExpiresAtUtc = null)
{
    public ContainerRegistryProfile Validate()
    {
        _ = OciImageReference.CanonicalRegistry(Registry);
        if (string.IsNullOrWhiteSpace(Username) || Username.Length > 256 || Username.Any(char.IsControl)
            || RepositoryPrefixes is null || RepositoryPrefixes.Count > 32)
            throw new ArgumentException("Use a registry username and up to 32 exact repository scopes.");
        foreach (var scope in RepositoryPrefixes)
            if (OciImageReference.ParseScope(scope).Registry != Registry) throw new ArgumentException("Repository scope must belong to the credential registry.");
        return this with { RepositoryPrefixes = Array.AsReadOnly(RepositoryPrefixes.Distinct(StringComparer.Ordinal).ToArray()) };
    }
    public bool Allows(OciImageReference image) => !image.IsLocal && image.Registry == Registry
        && (RepositoryPrefixes.Count == 0 || RepositoryPrefixes.Any(image.IsWithin));
}
