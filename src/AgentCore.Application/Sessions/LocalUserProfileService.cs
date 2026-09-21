using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed class LocalUserProfileService(
    IMemoryStore store,
    TimeProvider time,
    IProfileLiveUpdateNotifier? liveUpdates = null) : ILocalUserProfileService
{
    public async ValueTask<UserProfile> GetLocalProfileAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        var existing = await store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            LocalUserProfile.Validate(existing.Preferences);
            if (!LocalUserProfile.InventedPreferredNameNeedsRemoval(existing.Preferences))
            {
                return existing;
            }

            var cleaned = new UserProfile(
                existing.ProfileId,
                existing.Revision + 1,
                LocalUserProfile.WithoutInventedPreferredName(existing.Preferences),
                now);
            LocalUserProfile.Validate(cleaned.Preferences);
            try
            {
                await store.SaveProfileAsync(cleaned, existing.Revision, cancellationToken).ConfigureAwait(false);
                return cleaned;
            }
            catch (AgentCoreException ex) when (ex.Code == "Conflict")
            {
                var raced = await store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
                if (raced is null)
                {
                    throw;
                }

                LocalUserProfile.Validate(raced.Preferences);
                return raced with
                {
                    Preferences = LocalUserProfile.WithoutInventedPreferredName(raced.Preferences)
                };
            }
        }

        var created = new UserProfile(
            LocalUserProfile.Id,
            1,
            LocalUserProfile.CreateDefaultSeed(now),
            now);
        LocalUserProfile.Validate(created.Preferences);
        try
        {
            await store.SaveProfileAsync(created, 0, cancellationToken).ConfigureAwait(false);
            return created;
        }
        catch (AgentCoreException ex) when (ex.Code == "Conflict")
        {
            var raced = await store.LoadProfileAsync(LocalUserProfile.Id, cancellationToken).ConfigureAwait(false);
            if (raced is null)
            {
                throw;
            }

            LocalUserProfile.Validate(raced.Preferences);
            return raced;
        }
    }

    public async ValueTask<UserProfile> UpdateLocalProfileAsync(
        long expectedRevision,
        IReadOnlyDictionary<string, string?> values,
        UserProfileValueSource source,
        CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
        {
            throw AgentCoreErrors.Validation("At least one profile value is required.");
        }

        foreach (var key in values.Keys)
        {
            if (!LocalUserProfile.AllowedKeys.Any(allowed => string.Equals(allowed, key, StringComparison.Ordinal)))
            {
                throw AgentCoreErrors.Validation($"Profile key '{key}' is not allowlisted.");
            }
        }

        if (source is not (UserProfileValueSource.UserSet
            or UserProfileValueSource.HostSet
            or UserProfileValueSource.ApplicationProfile))
        {
            throw AgentCoreErrors.Validation("Profile value source is not supported.");
        }

        var current = await GetLocalProfileAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision)
        {
            throw AgentCoreErrors.Conflict("Stale profile revision.");
        }

        var now = time.GetUtcNow();
        var merged = new Dictionary<string, UserProfileValue>(current.Preferences, StringComparer.Ordinal);
        foreach (var pair in values)
        {
            if (pair.Value is null || string.IsNullOrWhiteSpace(pair.Value))
            {
                merged.Remove(pair.Key);
                continue;
            }

            try
            {
                LocalUserProfile.ValidateField(pair.Key, pair.Value);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                throw AgentCoreErrors.Validation(ex.Message);
            }

            var storedValue = string.Equals(pair.Key, "preferredName", StringComparison.Ordinal)
                ? pair.Value.Trim()
                : pair.Value;
            merged[pair.Key] = new UserProfileValue(storedValue, source, now);
        }

        try
        {
            LocalUserProfile.Validate(merged);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw AgentCoreErrors.Validation(ex.Message);
        }

        var updated = new UserProfile(
            current.ProfileId,
            current.Revision + 1,
            merged,
            now);
        await store.SaveProfileAsync(updated, expectedRevision, cancellationToken).ConfigureAwait(false);
        if (liveUpdates is not null)
        {
            using var notifyBudget = new CancellationTokenSource(ProfileLiveUpdateNotificationTimeout);
            try
            {
                await liveUpdates
                    .NotifyProfileUpdatedAsync(updated, notifyBudget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (notifyBudget.IsCancellationRequested)
            {
            }
        }

        return updated;
    }

    private static readonly TimeSpan ProfileLiveUpdateNotificationTimeout = TimeSpan.FromSeconds(30);
}
