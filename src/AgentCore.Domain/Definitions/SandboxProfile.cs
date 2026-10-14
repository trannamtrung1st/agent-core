using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore.Domain.Definitions;

/// <summary>Immutable environment revision. StorageBytes bounds canonical scratch, not the OCI writable layer.</summary>
public sealed record SandboxProfile(string Id, long Revision, string Name, string Description, bool Enabled,
    string ImageReference, string? InitializationScript = null, double Cpu = 1, int MemoryMiB = 1024,
    int Pids = 64, int MaxCommandSeconds = 120, int IdleTtlSeconds = 1800,
    int MaxLifetimeSeconds = 7200, long StorageBytes = 250L * 1024 * 1024,
    DateTimeOffset? LastVerifiedAt = null, string? VerificationReason = null)
{
    public void Validate()
    {
        if (!Regex.IsMatch(Id ?? "", "^[a-z][a-z0-9-]{0,63}$") || Revision < 1
            || string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || Description is null || Description.Length > 2000
            || !Regex.IsMatch(ImageReference ?? "", @"^(?:[a-zA-Z0-9./:_-]+@)?sha256:[a-f0-9]{64}$")
            || !ValidText(Name) || !ValidText(Description) || !ValidText(InitializationScript ?? "")
            || Encoding.UTF8.GetByteCount(InitializationScript ?? "") > 16384 || InitializationScript?.Contains('\0') == true
            || !double.IsFinite(Cpu) || Cpu is < 0.25 or > 2 || MemoryMiB is < 256 or > 2048
            || Pids != 64 || MaxCommandSeconds is < 1 or > 180 || IdleTtlSeconds is < 60 or > 3600
            || MaxLifetimeSeconds < IdleTtlSeconds || MaxLifetimeSeconds > 86400
            || StorageBytes is < 1 or > 250L * 1024 * 1024)
            throw new ArgumentException("Invalid sandbox profile: use immutable images and bounded environment limits.");
    }
    private static bool ValidText(string value)
    {
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return !value.Contains('\0'); }
        catch (EncoderFallbackException) { return false; }
    }
}

public sealed record SandboxPolicy(string DefaultProfileId, IReadOnlyList<string> AllowedProfileIds)
{
    public void Validate()
    {
        if (AllowedProfileIds is null || AllowedProfileIds.Count is < 1 or > 16 || AllowedProfileIds.Distinct(StringComparer.Ordinal).Count() != AllowedProfileIds.Count
            || !AllowedProfileIds.Contains(DefaultProfileId, StringComparer.Ordinal)
            || AllowedProfileIds.Any(id => id is null || !Regex.IsMatch(id, "^[a-z][a-z0-9-]{0,63}$")))
            throw new ArgumentException("Sandbox policy requires an exact allowed profile list containing its default.");
    }
}
