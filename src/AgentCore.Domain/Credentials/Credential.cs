using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace AgentCore.Domain.Credentials;

public enum CredentialKind { Password, ApiKey, Token, Certificate, PrivateKey, Generic }
public enum CredentialStatus { Active, Disabled }

public sealed record Credential(Guid CredentialId, string DisplayName, CredentialKind Kind,
    CredentialStatus Status, IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> AllowedOrigins,
    string ProtectedPayload, int ProtectionVersion, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record AgentCredentialBinding(Guid BindingId, Guid AgentInstanceId, Guid CredentialId,
    string Reference, long Revision, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public static class CredentialRules
{
    public static string DisplayName(string value)
    {
        value = value?.Trim() ?? "";
        if (value.Length is < 1 or > 120 || value.Any(char.IsControl)) throw new ArgumentException("Display name must contain 1–120 characters.");
        return value;
    }
    public static string Reference(string value)
    {
        value = value?.Trim().ToLowerInvariant() ?? "";
        if (value.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Reference must contain 1–64 letters, digits or hyphens.");
        return value;
    }
    public static IReadOnlyDictionary<string, string> Metadata(IReadOnlyDictionary<string, string>? input)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input?.Count > 32) throw new ArgumentException("Metadata has more than 32 entries.");
        foreach (var pair in input ?? new Dictionary<string, string>())
        {
            var key = pair.Key.Trim();
            if (key.Length is < 1 or > 64 || key.Any(char.IsControl) || pair.Value is null || pair.Value.Length > 2048
                || !result.TryAdd(key, pair.Value)) throw new ArgumentException("Metadata keys/values exceed bounds or contain ambiguous keys.");
        }
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result)) > 16 * 1024)
            throw new ArgumentException("Metadata exceeds 16 KiB.");
        return new ReadOnlyDictionary<string, string>(result);
    }
    public static IReadOnlyList<string> Origins(IReadOnlyList<string>? input)
    {
        if (input?.Count > 32) throw new ArgumentException("Origins exceed 32 entries.");
        return Array.AsReadOnly((input ?? []).Select(Origin).Distinct(StringComparer.Ordinal).ToArray());
    }
    public static string Origin(string input)
    {
        if (input is null || input.Length > 512 || !Uri.TryCreate(input, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/"
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.Host.Contains('*'))
            throw new ArgumentException("Use exact HTTP(S) origins without paths, credentials or wildcards.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
    public static void ProtectedValue(string value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 65536)
            throw new ArgumentException("Protected value must contain 1–65536 UTF-8 bytes.");
    }
}
