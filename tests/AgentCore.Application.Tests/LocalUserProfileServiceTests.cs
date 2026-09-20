using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class LocalUserProfileServiceTests
{
    [Fact]
    public async Task Get_creates_applicationProfile_language_seed()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var service = new LocalUserProfileService(new InMemoryMemoryStore(), time);
        var profile = await service.GetLocalProfileAsync();
        Assert.Equal("en", profile.Preferences["language"].Value);
        Assert.Equal(UserProfileValueSource.ApplicationProfile, profile.Preferences["language"].Source);
    }

    [Fact]
    public async Task Update_stamps_userSet_and_removes_null_fields()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var service = new LocalUserProfileService(new InMemoryMemoryStore(), time);
        var current = await service.GetLocalProfileAsync();
        var updated = await service.UpdateLocalProfileAsync(
            current.Revision,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["preferredName"] = "Sam",
                ["locale"] = "en-US"
            },
            UserProfileValueSource.UserSet);
        Assert.Equal(UserProfileValueSource.UserSet, updated.Preferences["preferredName"].Source);
        Assert.Equal("Sam", updated.Preferences["preferredName"].Value);

        var removed = await service.UpdateLocalProfileAsync(
            updated.Revision,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["preferredName"] = null },
            UserProfileValueSource.UserSet);
        Assert.False(removed.Preferences.ContainsKey("preferredName"));
    }

    [Fact]
    public async Task Update_allows_trusted_hostSet_from_application_layer()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var service = new LocalUserProfileService(new InMemoryMemoryStore(), time);
        var current = await service.GetLocalProfileAsync();
        var updated = await service.UpdateLocalProfileAsync(
            current.Revision,
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["timeZone"] = "UTC" },
            UserProfileValueSource.HostSet);
        Assert.Equal(UserProfileValueSource.HostSet, updated.Preferences["timeZone"].Source);
    }

    [Fact]
    public async Task Update_persists_userSet_through_sqlite_reopen()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var path = Path.Combine(Path.GetTempPath(), $"agent-core-profile-api-{Guid.NewGuid():N}.db");
        try
        {
            await using var harness = SqliteTestHarness.Open(path, deleteOnDispose: false);
            await harness.Store.EnsureCreatedAsync();
            var service = new LocalUserProfileService(harness.Store, time);
            var current = await service.GetLocalProfileAsync();
            await service.UpdateLocalProfileAsync(
                current.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["preferredName"] = "Sam" },
                UserProfileValueSource.UserSet);

            await using var reopened = SqliteTestHarness.Open(path, deleteOnDispose: true);
            await reopened.Store.EnsureCreatedAsync();
            var loaded = await reopened.Store.LoadProfileAsync(LocalUserProfile.Id);
            Assert.Equal("Sam", loaded!.Preferences["preferredName"].Value);
            Assert.Equal(UserProfileValueSource.UserSet, loaded.Preferences["preferredName"].Source);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection($"Data Source={path}"));
        }
    }

    [Fact]
    public async Task Update_rejects_unknown_keys_and_stale_revision()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
        var service = new LocalUserProfileService(new InMemoryMemoryStore(), time);
        var current = await service.GetLocalProfileAsync();
        var stale = await Assert.ThrowsAsync<AgentCoreException>(() =>
            service.UpdateLocalProfileAsync(
                current.Revision + 5,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["language"] = "fr" },
                UserProfileValueSource.UserSet).AsTask());
        Assert.Equal("Conflict", stale.Code);

        var unknown = await Assert.ThrowsAsync<AgentCoreException>(() =>
            service.UpdateLocalProfileAsync(
                current.Revision,
                new Dictionary<string, string?>(StringComparer.Ordinal) { ["nickname"] = "x" },
                UserProfileValueSource.UserSet).AsTask());
        Assert.Equal("ValidationError", unknown.Code);
    }
}
