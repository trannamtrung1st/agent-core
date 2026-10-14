using System.Text.RegularExpressions;

namespace AgentCore.Domain.Definitions;

/// <summary>Canonical image identity and segment-boundary registry authorization. No implicit Docker Hub names.</summary>
public sealed record OciImageReference(string Value, string? Registry, string? Repository, string Digest)
{
    public bool IsLocal => Registry is null;
    public static OciImageReference Parse(string value)
    {
        if (value is null || value.Length > 1024 || value.Any(c => c > 127 || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Use an explicit registry/repository@sha256 digest or an approved local sha256 image ID.");
        if (Regex.IsMatch(value, "^sha256:[a-f0-9]{64}$")) return new(value, null, null, value[7..]);
        var parts = value.Split('@');
        if (parts.Length != 2 || !Regex.IsMatch(parts[1], "^sha256:[a-f0-9]{64}$"))
            throw new ArgumentException("Mutable tags are not supported. Use an immutable sha256 digest.");
        var (registry, repository) = ParseScope(parts[0]);
        if (repository.Length == 0) throw new ArgumentException("Remote images require an explicit repository path.");
        return new(registry + "/" + repository + "@" + parts[1], registry, repository, parts[1][7..]);
    }

    public static (string Registry, string Repository) ParseScope(string value)
    {
        if (value is null || value.Length > 900 || value != value.Trim() || value.Any(c => c > 127 || char.IsControl(c)))
            throw new ArgumentException("Invalid registry/repository scope.");
        value = value.TrimEnd('/');
        var slash = value.IndexOf('/');
        var registry = CanonicalRegistry(slash < 0 ? value : value[..slash]);
        var repository = slash < 0 ? "" : value[(slash + 1)..];
        if (slash >= 0 && (repository.Length == 0 || repository.Split('/').Any(p => !Regex.IsMatch(p, "^[a-z0-9]+(?:(?:[._]|__|[-]+)[a-z0-9]+)*$"))))
            throw new ArgumentException("Repository scopes must use complete lowercase path segments.");
        return (registry, repository);
    }

    public static string CanonicalRegistry(string value)
    {
        if (value is null || value.Length > 253 || value != value.ToLowerInvariant()) throw new ArgumentException("Use a canonical lowercase registry host.");
        var hostPort = value.Split(':');
        if (hostPort.Length > 2 || hostPort.Length == 2 && (!Regex.IsMatch(hostPort[1], "^[1-9][0-9]{0,4}$") || !int.TryParse(hostPort[1], out var port) || port > 65535))
            throw new ArgumentException("Invalid registry port.");
        var host = hostPort[0];
        if (host != "localhost" && !host.Contains('.') || host.Split('.').Any(label => !Regex.IsMatch(label, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")))
            throw new ArgumentException("Use an explicit registry host without URLs, credentials or wildcards.");
        return value;
    }

    public bool IsWithin(string scope)
    {
        var (host, path) = ParseScope(scope);
        return !IsLocal && Registry == host && (path.Length == 0 || Repository == path || Repository!.StartsWith(path + "/", StringComparison.Ordinal));
    }
}
