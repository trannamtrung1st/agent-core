using AgentCore.Application.Sessions;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Memory;

namespace AgentCore.Infrastructure.Persistence;

internal static class IdentityConsolidationSemantics
{
    internal static void Sources(IEnumerable<Guid> ids, IEnumerable<Guid>? lineage)
    {
        var selected = ids.ToArray();
        if (selected.Length is < IdentityMaintenanceLimits.MinSources or > IdentityMaintenanceLimits.MaxSources
            || selected.Any(id => id == Guid.Empty) || selected.Distinct().Count() != selected.Length
            || lineage is null || !selected.Order().SequenceEqual(lineage))
            throw AgentCoreErrors.Validation("Consolidation requires two to eight unique sources and exact sorted lineage.");
    }

    internal static bool SameOwner(StructuredMemoryItem a, StructuredMemoryItem b) => a.Scope == b.Scope && a.Scope switch
    {
        MemoryScope.Session => a.SessionId == b.SessionId,
        MemoryScope.IdentityUser => a.OwnerInstanceId is not null && a.OwnerInstanceId == b.OwnerInstanceId
            && a.OwnerProfileId is not null && a.OwnerProfileId == b.OwnerProfileId,
        MemoryScope.User => a.OwnerProfileId is not null && a.OwnerProfileId == b.OwnerProfileId,
        _ => false
    };

    internal static void Memory(IReadOnlyList<StructuredMemoryItem> sources, StructuredMemoryItem result)
    {
        Sources(sources.Select(s => s.MemoryId), result.Provenance.DerivedFromMemoryIds);
        if (result.Status != MemoryItemStatus.Active || result.MemoryId == Guid.Empty
            || sources.Any(s => s.Status == MemoryItemStatus.Deleted || s.Kind != result.Kind || !SameOwner(s, result)))
            throw AgentCoreErrors.Conflict("Memory consolidation sources must be active with the same owner, scope and kind.");
    }

    internal static bool MemoryUnchanged(StructuredMemoryItem current, StructuredMemoryItem expected) =>
        current.Status == MemoryItemStatus.Active && SameOwner(current, expected) && current.Kind == expected.Kind
        && current.Subject == expected.Subject && current.Content == expected.Content;

    internal static void Experience(IReadOnlyList<AgentExperience> sources, AgentExperience result)
    {
        Sources(sources.Select(s => s.ExperienceId), result.DerivedFromExperienceIds);
        if (result.SourceKind != ExperienceSourceKind.Consolidation || result.Visibility != ExperienceVisibility.Eligible
            || result.Content is null || sources.Any(s => s.AgentInstanceId != result.AgentInstanceId || s.ProfileId != result.ProfileId
                || s.Visibility is ExperienceVisibility.Deleted or ExperienceVisibility.Suppressed || s.Content is null))
            throw AgentCoreErrors.Conflict("Experience consolidation requires completed eligible sources owned by this instance.");
        result.Content.Validate();
    }
}
