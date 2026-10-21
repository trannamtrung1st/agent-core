using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantExperienceFactRules
{
    public static TenantExperienceFactObservation Observe(TenantMemoryReader reader, TenantIsolationMetadata settings, TenantContinuityArguments args,
        IEnumerable<TenantExperience> candidates, PreparedExperienceRead? expected, bool human, bool candidateMore = false)
    {
        var references = expected is null ? null : Sources(expected); var named = references?.Select(i => i.ExperienceId).ToHashSet();
        var selected = candidates.Where(i => TenantExperienceRules.Owns(reader, i.Reference()) && (named is not null ? named.Contains(i.ExperienceId)
                : i.Visibility == ExperienceVisibility.Eligible && i.Content is not null && (args.Id is null || i.ExperienceId == args.Id)))
            .Select(i => (Item: TenantExperienceRules.Copy(i), Score: AgentCore.Application.Continuity.ContinuityService.Score(TenantExperienceRules.SearchText(i), args.Query)))
            .Where(i => named is not null || args.Kind == "Experience" || string.IsNullOrWhiteSpace(args.Query) || i.Score > 0)
            .OrderByDescending(i => i.Score).ThenByDescending(i => i.Item.UpdatedAt).ThenBy(i => i.Item.ExperienceId).Take(named?.Count ?? args.Limit + 1).Select(i => i.Item).ToArray();
        if (args.Kind == "Experience" && selected.Length != 1 || named is not null && selected.Length != named.Count) throw new AccessDeniedException("experience_source_unavailable");
        var json = JsonSerializer.Serialize(selected.Select(i => i.Reference()));
        var labels = JsonSerializer.Serialize(selected.Select(i => new { id = i.ExperienceId, goal = TenantContinuityRules.Safe(i.Content?.Goal ?? "Content removed", 80, out _), scope = i.UserPrincipalId is null ? "Instance" : "AgentUser" }));
        var value = new PreparedExperienceRead(reader.Run.SessionId, reader.Root.SessionId, reader.User?.PrincipalId, args.Tool, args.CanonicalArguments, json, labels,
            TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, sources = json })), settings.ExperienceRevision);
        var changed = expected is not null && value != expected;
        if (changed && !human) throw new AccessDeniedException("experience_source_changed");
        return new(reader, settings, selected, human ? expected! : value, candidateMore || selected.Length > args.Limit, changed);
    }
    public static void Validate(PreparedContinuityRead read, PreparedExperienceRead value)
    {
        var args = TenantContinuityToolContract.Parse(value.ToolName, value.ArgumentsJson); var sources = Sources(value);
        if (value.SessionId != read.SessionId || value.RootSessionId != read.RootSessionId || value.UserPrincipalId != read.UserPrincipalId || value.ToolName != read.ToolName
            || value.ArgumentsJson != read.ArgumentsJson || value.ExperienceRevision < 1 || args.Kind is "Memory" or "Session"
            || args.Kind == "Experience" && (sources.Length != 1 || sources[0].ExperienceId != args.Id)
            || args.Kind is null && sources.Length > args.Limit + 1 || value.ObservationHash != TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { args.Tool, args.CanonicalArguments, sources = value.SourcesJson })))
            throw new ArgumentException("Use an exact Experience continuity observation.");
        using var labels = JsonDocument.Parse(value.SourceLabelsJson);
        if (System.Text.Encoding.UTF8.GetByteCount(value.SourceLabelsJson) > 8192 || labels.RootElement.ValueKind != JsonValueKind.Array || labels.RootElement.GetArrayLength() != sources.Length)
            throw new ArgumentException("Use bounded Experience source labels.");
        var n = 0;
        foreach (var label in labels.RootElement.EnumerateArray())
        {
            TenantApprovalToolContract.Require(label, ["id", "goal", "scope"]); var source = sources[n++];
            if (label.GetProperty("id").GetGuid() != source.ExperienceId || label.GetProperty("goal").ValueKind != JsonValueKind.String || label.GetProperty("goal").GetString()!.Length > 80
                || label.GetProperty("scope").GetString() != (source.UserPrincipalId is null ? "Instance" : "AgentUser")) throw new ArgumentException("Use exact Experience labels.");
        }
    }
    public static TenantExperienceReference[] Sources(PreparedExperienceRead read)
    {
        var sources = JsonSerializer.Deserialize<TenantExperienceReference[]>(read.SourcesJson) ?? throw new ArgumentException("Use exact Experience facts.");
        if (sources.Length > 11 || sources.Select(i => i.ExperienceId).Distinct().Count() != sources.Length || sources.Any(i => i.ExperienceId == Guid.Empty || i.TenantId == Guid.Empty || i.Revision < 1
            || !TenantMemoryOperationRules.ValidHash(i.ContentHash) || !Enum.IsDefined(i.Visibility)) || JsonSerializer.Serialize(sources) != read.SourcesJson) throw new ArgumentException("Use bounded canonical Experience facts."); return sources;
    }
}
