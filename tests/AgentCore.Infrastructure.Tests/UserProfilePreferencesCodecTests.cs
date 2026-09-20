using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentCore.Infrastructure.Tests;

public sealed class UserProfilePreferencesCodecTests
{
    private static readonly DateTimeOffset SeedTime = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Write_roundtrips_typed_value_source_and_updatedAt()
    {
        var preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
        {
            ["language"] = new UserProfileValue("en", UserProfileValueSource.UserSet, SeedTime),
            ["locale"] = new UserProfileValue("en-US", UserProfileValueSource.HostSet, SeedTime.AddMinutes(1))
        };

        var json = UserProfilePreferencesCodec.Write(preferences);
        var loaded = UserProfilePreferencesCodec.Read(json, SeedTime);

        Assert.Equal(UserProfileValueSource.UserSet, loaded["language"].Source);
        Assert.Equal(SeedTime, loaded["language"].UpdatedAt);
        Assert.Equal(UserProfileValueSource.HostSet, loaded["locale"].Source);
        Assert.Equal(SeedTime.AddMinutes(1), loaded["locale"].UpdatedAt);
    }

    [Fact]
    public void Read_maps_legacy_strings_to_applicationProfile_with_fallback_timestamp()
    {
        const string legacy = """{"language":"en","preferredName":"Pat"}""";
        var loaded = UserProfilePreferencesCodec.Read(legacy, SeedTime);

        Assert.Equal(UserProfileValueSource.ApplicationProfile, loaded["language"].Source);
        Assert.Equal(SeedTime, loaded["language"].UpdatedAt);
        Assert.Equal("Pat", loaded["preferredName"].Value);
    }

    [Fact]
    public void Read_discards_legacy_friend_and_mixed_typed_json_loads()
    {
        const string legacyFriend = """{"language":"en","preferredName":"friend"}""";
        var withoutFriend = UserProfilePreferencesCodec.Read(legacyFriend, SeedTime);
        Assert.False(withoutFriend.ContainsKey("preferredName"));

        const string mixed = """
            {
              "language": "en",
              "locale": {
                "value": "en-US",
                "source": "userSet",
                "updatedAt": "2026-09-20T01:00:00Z"
              }
            }
            """;
        var typed = UserProfilePreferencesCodec.Read(mixed, SeedTime);
        Assert.Equal(UserProfileValueSource.ApplicationProfile, typed["language"].Source);
        Assert.Equal(UserProfileValueSource.UserSet, typed["locale"].Source);
    }

    [Fact]
    public async Task InMemory_roundtrip_profile_provenance()
    {
        var now = SeedTime;
        var preferences = new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
        {
            ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
            ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now.AddMinutes(2))
        };
        var profile = new UserProfile(LocalUserProfile.Id, 1, preferences, now);

        var memory = new InMemoryMemoryStore();
        await memory.SaveProfileAsync(profile, 0);
        var fromMemory = await memory.LoadProfileAsync(LocalUserProfile.Id);
        Assert.Equal(UserProfileValueSource.UserSet, fromMemory!.Preferences["preferredName"].Source);
        Assert.Equal(now.AddMinutes(2), fromMemory.Preferences["preferredName"].UpdatedAt);
    }
}
