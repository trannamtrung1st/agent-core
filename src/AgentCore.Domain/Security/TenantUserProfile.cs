using System.Collections.ObjectModel;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Security;

public sealed record TenantUserProfile(Guid TenantId, Guid PrincipalId, Guid AccountId, long Revision,
    IReadOnlyDictionary<string, UserProfileValue> Preferences, DateTimeOffset UpdatedAt)
{
    public static readonly IReadOnlyList<string> Fields = Array.AsReadOnly(new[] { "preferredName", "language", "locale", "timeZone" });
    public static void ValidateField(string key, string value)
    {
        var valid = key switch {
            "preferredName" => value.Length <= 128,
            "language" => value.Length <= 32 && UserProfileFieldPatterns.IsLanguageTag(value),
            "locale" => value.Length <= 35 && UserProfileFieldPatterns.IsLocaleTag(value),
            "timeZone" => value.Length <= 64 && UserProfileFieldPatterns.IsTimeZoneId(value),
            _ => false };
        if (!valid || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new ArgumentException("Invalid bounded User Profile field.");
    }
    public TenantUserProfile Freeze()
    {
        if (TenantId == Guid.Empty || PrincipalId == Guid.Empty || AccountId == Guid.Empty || Revision < 0
            || UpdatedAt < DateTimeOffset.UnixEpoch || UpdatedAt.Offset != TimeSpan.Zero || UpdatedAt != DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAt.ToUnixTimeMilliseconds()) || Preferences is null || Preferences.Count > Fields.Count
            || Revision == 0 && Preferences.Count != 0) throw new ArgumentException("Invalid owned User Profile.");
        foreach (var (key, value) in Preferences)
        {
            if (value is null) throw new ArgumentException("Invalid User Profile value.");
            ValidateField(key, value.Value);
            if (value.Source != UserProfileValueSource.UserSet || value.UpdatedAt.Offset != TimeSpan.Zero || value.UpdatedAt > UpdatedAt
                || value.UpdatedAt != DateTimeOffset.FromUnixTimeMilliseconds(value.UpdatedAt.ToUnixTimeMilliseconds()))
                throw new ArgumentException("Invalid User Profile provenance.");
        }
        return this with { Preferences = new ReadOnlyDictionary<string, UserProfileValue>(new Dictionary<string, UserProfileValue>(Preferences, StringComparer.Ordinal)) };
    }
}
