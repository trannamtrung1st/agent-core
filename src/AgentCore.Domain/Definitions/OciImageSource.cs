using System.Text.RegularExpressions;

namespace AgentCore.Domain.Definitions;

/// <summary>Owner input only. For tags, Image carries a zero digest solely for scope validation; resolve before storage.</summary>
public sealed record OciImageSource(OciImageReference Image, string? Tag)
{
    public static OciImageSource Parse(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is 0 or > 1024 || value.Any(c => c > 127 || char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Enter an image name with a tag or sha256 digest.");
        if (value.StartsWith("sha256:", StringComparison.Ordinal)) return new(OciImageReference.Parse(value), null);
        string? tag = null;
        var digest = value.IndexOf('@');
        var repository = digest < 0 ? value : value[..digest];
        if (digest < 0)
        {
            var colon = repository.LastIndexOf(':');
            if (colon > repository.LastIndexOf('/'))
            {
                tag = repository[(colon + 1)..];
                repository = repository[..colon];
            }
            else tag = "latest";
            if (!Regex.IsMatch(tag, "^[a-zA-Z0-9_][a-zA-Z0-9_.-]{0,127}$"))
                throw new ArgumentException("Use a valid image tag.");
        }
        var first = repository.Split('/')[0];
        if (!repository.Contains('/') || !first.Contains('.') && !first.Contains(':') && first != "localhost") repository = "docker.io/" + repository;
        if (repository.StartsWith("docker.io/", StringComparison.Ordinal) && repository.Count(c => c == '/') == 1)
            repository = "docker.io/library/" + repository[10..];
        return new(OciImageReference.Parse(repository + (digest < 0 ? "@sha256:" + new string('0', 64) : value[digest..])), tag);
    }
}
