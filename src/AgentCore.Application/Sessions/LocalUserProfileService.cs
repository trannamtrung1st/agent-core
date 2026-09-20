using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed class LocalUserProfileService(IMemoryStore store, TimeProvider time) : ILocalUserProfileService
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

            merged[pair.Key] = new UserProfileValue(pair.Value, source, now);
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
        return updated;
    }
}
