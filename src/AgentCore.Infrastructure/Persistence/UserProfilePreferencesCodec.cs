using System.Text.Json;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

internal static class UserProfilePreferencesCodec
{
    private static readonly JsonSerializerOptions WriteJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static IReadOnlyDictionary<string, UserProfileValue> Read(
        string preferencesJson,
        DateTimeOffset legacyFallbackUpdatedAt)
    {
        if (string.IsNullOrWhiteSpace(preferencesJson))
        {
            return new Dictionary<string, UserProfileValue>(StringComparer.Ordinal);
        }

        using var document = JsonDocument.Parse(preferencesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Profile preferences must be a JSON object.");
        }

        var preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                var legacyValue = property.Value.GetString() ?? string.Empty;
                if (ShouldSkipLegacyEntry(property.Name, legacyValue))
                {
                    continue;
                }

                preferences[property.Name] = new UserProfileValue(
                    legacyValue,
                    UserProfileValueSource.ApplicationProfile,
                    legacyFallbackUpdatedAt);
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException($"Profile preference '{property.Name}' must be a string or typed object.");
            }

            var typed = ReadTyped(property.Name, property.Value);
            if (typed is not null)
            {
                preferences[property.Name] = typed;
            }
        }

        return preferences;
    }

    public static string Write(IReadOnlyDictionary<string, UserProfileValue> preferences)
    {
        var payload = new Dictionary<string, TypedPreferenceJson>(StringComparer.Ordinal);
        foreach (var pair in preferences)
        {
            payload[pair.Key] = new TypedPreferenceJson
            {
                Value = pair.Value.Value,
                Source = ToWireSource(pair.Value.Source),
                UpdatedAt = pair.Value.UpdatedAt
            };
        }

        return JsonSerializer.Serialize(payload, WriteJson);
    }

    private static UserProfileValue? ReadTyped(string key, JsonElement element)
    {
        if (!element.TryGetProperty("value", out var valueElement)
            || valueElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Profile preference '{key}' is missing a string value.");
        }

        var value = valueElement.GetString() ?? string.Empty;
        if (ShouldSkipLegacyEntry(key, value))
        {
            return null;
        }

        if (!element.TryGetProperty("source", out var sourceElement)
            || sourceElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Profile preference '{key}' is missing source.");
        }

        if (!element.TryGetProperty("updatedAt", out var updatedAtElement))
        {
            throw new JsonException($"Profile preference '{key}' is missing updatedAt.");
        }

        var updatedAt = updatedAtElement.ValueKind switch
        {
            JsonValueKind.String => DateTimeOffset.Parse(
                updatedAtElement.GetString()!,
                null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            _ => throw new JsonException($"Profile preference '{key}' updatedAt must be an ISO-8601 string.")
        };

        return new UserProfileValue(value, ParseWireSource(sourceElement.GetString()!), updatedAt);
    }

    private static bool ShouldSkipLegacyEntry(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return string.Equals(key, "preferredName", StringComparison.Ordinal)
            && string.Equals(value.Trim(), LocalUserProfile.InventedPreferredNameSeed, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToWireSource(UserProfileValueSource source) =>
        source switch
        {
            UserProfileValueSource.UserSet => "userSet",
            UserProfileValueSource.HostSet => "hostSet",
            UserProfileValueSource.ApplicationProfile => "applicationProfile",
            _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
        };

    private static UserProfileValueSource ParseWireSource(string wire) =>
        wire switch
        {
            "userSet" => UserProfileValueSource.UserSet,
            "hostSet" => UserProfileValueSource.HostSet,
            "applicationProfile" => UserProfileValueSource.ApplicationProfile,
            _ => throw new JsonException($"Unknown profile value source '{wire}'.")
        };

    private sealed class TypedPreferenceJson
    {
        public string Value { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public DateTimeOffset UpdatedAt { get; set; }
    }
}
