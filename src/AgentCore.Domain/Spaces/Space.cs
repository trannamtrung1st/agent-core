using System.Collections.ObjectModel;

namespace AgentCore.Domain.Spaces;

public enum SpaceKind { Private, Shared }
public enum SpaceBindingKind { DefinitionResource, InstanceResource, SpaceFile, Credential, Secret }

/// <summary>Stable source identity. Display names, paths and aliases never confer authority.</summary>
public sealed record SpaceResourceBinding(SpaceBindingKind Kind, string ResourceKey, string? AccountRef = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(ResourceKey) || ResourceKey.Length > 256 || ResourceKey.Any(char.IsControl))
            throw new ArgumentException("Invalid resource identity.");
        if (Kind is SpaceBindingKind.Credential or SpaceBindingKind.Secret
            && (!Guid.TryParseExact(ResourceKey, "D", out var id) || id == Guid.Empty)) throw new ArgumentException("A stable protected resource ID is required.");
        if (Kind == SpaceBindingKind.SpaceFile && !SpaceResourceKeys.TryParse(ResourceKey, out _, out _))
            throw new ArgumentException("A canonical Space resource identity is required.");
        if (AccountRef is not null && (Kind != SpaceBindingKind.Credential || AccountRef.Length is < 1 or > 64
            || AccountRef.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))) throw new ArgumentException("Invalid contextual account alias.");
        if (AccountRef is not null && AccountRef != AgentCore.Domain.Credentials.CredentialRules.Reference(AccountRef)) throw new ArgumentException("Use a normalized contextual account alias.");
        if (Kind == SpaceBindingKind.Credential && AccountRef is null) throw new ArgumentException("A contextual account alias is required.");
    }
    public string StableKey => $"{Kind}:{ResourceKey}";
}

public sealed record Space(Guid TenantId, Guid SpaceId, SpaceKind Kind, Guid? OwnerInstanceId, string Name,
    string Description, string Instructions, bool EnvironmentsEnabled, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt = null, long EnvironmentSelectionEpoch = 0)
{
    public void Validate()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || !Enum.IsDefined(Kind) || Revision < 1
            || EnvironmentSelectionEpoch < 0 || (Kind == SpaceKind.Private) != (OwnerInstanceId is not null) || OwnerInstanceId == Guid.Empty)
            throw new ArgumentException("Invalid Space ownership.");
        SpaceText.Require(Name, 160, required: true); SpaceText.Require(Description, 2048); SpaceText.Require(Instructions, 16384);
        if (UpdatedAt < CreatedAt) throw new ArgumentException("Invalid Space timestamps.");
    }
}

public sealed record SpaceMembership(Guid TenantId, Guid SpaceId, Guid InstanceId, string Responsibility, string Instructions,
    IReadOnlyList<string> DisabledInheritedBindingKeys, IReadOnlyList<SpaceResourceBinding> AddedBindings,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    public SpaceMembership Freeze()
    {
        if (TenantId == Guid.Empty || SpaceId == Guid.Empty || InstanceId == Guid.Empty || Revision < 1 || UpdatedAt < CreatedAt)
            throw new ArgumentException("Invalid Membership ownership.");
        SpaceText.Require(Responsibility, 160); SpaceText.Require(Instructions, 16384);
        if (DisabledInheritedBindingKeys.Count > 256 || AddedBindings.Count > 256
            || DisabledInheritedBindingKeys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 320 || k.Any(char.IsControl))
            || DisabledInheritedBindingKeys.Distinct(StringComparer.Ordinal).Count() != DisabledInheritedBindingKeys.Count)
            throw new ArgumentException("Invalid Membership exclusions.");
        foreach (var binding in AddedBindings) binding.Validate();
        if (AddedBindings.Select(b => b.StableKey).Distinct(StringComparer.Ordinal).Count() != AddedBindings.Count
            || AddedBindings.Where(b => b.AccountRef is not null).Select(b => b.AccountRef).Distinct(StringComparer.Ordinal).Count() != AddedBindings.Count(b => b.AccountRef is not null))
            throw new ArgumentException("Duplicate Membership resource or account alias.");
        return this with { DisabledInheritedBindingKeys = Array.AsReadOnly(DisabledInheritedBindingKeys.ToArray()), AddedBindings = Array.AsReadOnly(AddedBindings.ToArray()) };
    }
}

/// <summary>A computed restriction/extension of bindings, never a saved permission grant.</summary>
public static class EffectiveSpaceBindings
{
    public static IReadOnlyList<SpaceResourceBinding> Resolve(IReadOnlyList<SpaceResourceBinding> inherited, SpaceMembership membership)
    {
        membership = membership.Freeze();
        foreach (var binding in inherited) binding.Validate();
        var disabled = membership.DisabledInheritedBindingKeys.ToHashSet(StringComparer.Ordinal);
        var combined = inherited.Where(b => !disabled.Contains(b.StableKey)).Concat(membership.AddedBindings).ToArray();
        foreach (var group in combined.GroupBy(b => b.StableKey, StringComparer.Ordinal))
            if (group.Select(b => b.AccountRef).Distinct(StringComparer.Ordinal).Count() > 1) throw new ArgumentException("Conflicting aliases for one resource.");
        var result = combined.DistinctBy(b => b.StableKey, StringComparer.Ordinal).ToArray();
        if (result.Where(b => b.AccountRef is not null).GroupBy(b => b.AccountRef, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new ArgumentException("Contextual account alias collision.");
        return Array.AsReadOnly(result);
    }

    public static SpaceMembership Prune(SpaceMembership membership, IReadOnlyList<SpaceResourceBinding> inherited)
    {
        var baseline = inherited.Select(b => b.StableKey).ToHashSet(StringComparer.Ordinal);
        return (membership with { DisabledInheritedBindingKeys = membership.DisabledInheritedBindingKeys.Where(baseline.Contains).ToArray() }).Freeze();
    }
}

internal static class SpaceText
{
    public static void Require(string value, int limit, bool required = false)
    {
        if (value is null || value.Length > limit || required && string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
            throw new ArgumentException("Invalid Space text.");
    }
}
