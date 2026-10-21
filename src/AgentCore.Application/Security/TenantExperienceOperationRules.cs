using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantExperienceOperationRules
{
    public static TenantExperienceOperationObservation Observe(TenantMemoryReader reader, TenantIsolationMetadata settings, TenantExperienceArguments args,
        TenantExperienceSourceObservation? source, IReadOnlyList<TenantExperience> items, TenantExperience? existing = null, PreparedExperienceOperation? expected = null, bool inspection = false)
    {
        if (!inspection) TenantExperienceRules.Eligible(reader, settings, args.Tool);
        if (args.Tool is TenantExperienceToolContract.Source or TenantExperienceToolContract.Record)
        {
            if (source is null || source.Witness.Kind != args.SourceKind || source.Witness.SourceId != args.SourceId
                || !inspection && args.Tool == TenantExperienceToolContract.Record && source.Witness.ThroughCursor != args.ThroughCursor) throw new AccessDeniedException("experience_source_changed");
        }
        if (args.Content is { } content) _ = TenantExperienceRules.Freeze(content);
        if (args.Tool == TenantExperienceToolContract.Consolidate)
        {
            if (args.SourceIds is null || items.Count != args.SourceIds.Count || items.Count is < 2 or > 8 || items.Any(i => !args.SourceIds.Contains(i.ExperienceId)
                || !TenantExperienceRules.Owns(reader, i.Reference()) || i.UserPrincipalId != reader.User?.PrincipalId)
                || !inspection && existing is null && items.Any(i => i.Visibility != ExperienceVisibility.Eligible || i.Content is null)) throw new AccessDeniedException("experience_sources_unavailable");
        }
        var sources = JsonSerializer.Serialize(items.OrderBy(i => i.ExperienceId).Select(i => i.Reference()));
        var labels = JsonSerializer.Serialize(source is null ? items.OrderBy(i => i.ExperienceId).Select(i => new { id = i.ExperienceId, title = TenantContinuityRules.Safe(i.Content?.Goal ?? "Content removed", 80, out _), kind = i.SourceKind.ToString() }).ToArray()
            : [new { id = source.Witness.SourceId, title = source.Title, kind = source.Witness.Kind.ToString() }]);
        var result = args.ReadOnly ? (Guid?)null : TenantExperienceRules.ResultId(reader, args);
        var value = new PreparedExperienceOperation(reader.Run.SessionId, reader.Root.SessionId, reader.User?.PrincipalId, args.Tool, args.CanonicalArguments, sources, labels,
            TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, source = source?.Witness, sources })), settings.ExperienceRevision, args.Tool == TenantExperienceToolContract.Consolidate ? settings.MaintenanceRevision : 0, source?.Witness, result);
        var changed = expected is not null && value != expected;
        if (changed && !inspection) throw new AccessDeniedException("experience_source_changed");
        if (!inspection && existing is not null && (!TenantExperienceRules.Owns(reader, existing.Reference()) || existing.ContentHash != TenantExperienceRules.ContentHash(args.Content!)))
            throw new SecurityConflictException("experience_source_already_recorded");
        return new(reader, settings, args, source, items, expected ?? value, existing, changed);
    }
    public static IReadOnlyList<TenantExperience> Values(TenantExperienceOperationObservation observation, DateTimeOffset at)
    {
        if (observation.Arguments.ReadOnly || observation.Value.ResultExperienceId is not { } id || observation.Arguments.Content is null) return [];
        if (observation.ExistingResult is not null) return [];
        var reader = observation.Reader; var content = TenantExperienceRules.Freeze(observation.Arguments.Content); var consolidated = observation.Arguments.Tool == TenantExperienceToolContract.Consolidate;
        var value = new TenantExperience(reader.Security.Tenant.TenantId, id, reader.Run.AgentInstanceId, reader.Agent.PrincipalId, reader.User?.PrincipalId, reader.User?.AccountId, reader.User?.UserId,
            reader.Run.SessionId, reader.Run.AgentRunId, reader.Root.AgentRunId, consolidated ? ExperienceSourceKind.Consolidation : observation.Source!.Witness.Kind,
            consolidated ? id : observation.Source!.Witness.SourceId, consolidated ? 0 : observation.Source!.Witness.ThroughCursor,
            consolidated ? "[]" : JsonSerializer.Serialize(observation.Source!.Witness), content, TenantExperienceRules.ContentHash(content), ExperienceVisibility.Eligible, 1, at, at,
            consolidated ? JsonSerializer.Serialize(observation.Items.Select(i => i.ExperienceId).Order()) : null);
        return consolidated ? observation.Items.Select(i => i with { Visibility = ExperienceVisibility.Superseded, Revision = checked(i.Revision + 1), UpdatedAt = at }).Append(value).ToArray() : [value];
    }
}
