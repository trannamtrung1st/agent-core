using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Experience;

public enum ExperienceSourceKind { Session = 0, AgentRun = 1, Consolidation = 2 }
public enum ExperienceVisibility { Eligible = 0, Suppressed = 1, Deleted = 2, Superseded = 3 }

public sealed record ExperienceSettings(Guid AgentInstanceId, bool Enabled, long Revision);

/// <summary>Derived observations, never learned memory, instructions or authorization.</summary>
public sealed record ExperienceContent(string Goal, string[] Attempts, string[] Decisions,
    string[] Outcomes, string[] Corrections, string[] Unresolved, string[] Difficulties, string[] Lessons)
{
    public const int MaxFieldCharacters = 600;
    public const int MaxTotalCharacters = 6000;
    public void Validate()
    {
        var sections = new[] { Attempts, Decisions, Outcomes, Corrections, Unresolved, Difficulties, Lessons };
        if (string.IsNullOrWhiteSpace(Goal) || Goal.Length > MaxFieldCharacters
            || sections.Any(s => s is null || s.Length > 6)
            || sections.SelectMany(s => s).Any(s => string.IsNullOrWhiteSpace(s) || s.Length > MaxFieldCharacters)
            || JsonSerializer.Serialize(this).Length > MaxTotalCharacters)
            throw new ArgumentException("Retrospective output is malformed or exceeds its bounds.");
    }
}

public sealed record AgentExperience(Guid ExperienceId, Guid AgentInstanceId, Guid ProfileId,
    ExperienceSourceKind SourceKind, Guid SourceId, long ThroughCursor, DateTimeOffset SourceAtUtc,
    string DefinitionId, int DefinitionVersion, Guid GenerationAgentRunId, AgentRunModelPin Model,
    DateTimeOffset CreatedAtUtc, ExperienceContent? Content = null,
    ExperienceVisibility Visibility = ExperienceVisibility.Eligible, long Revision = 1,
    string? GenerationDefinitionId = null, int? GenerationDefinitionVersion = null, AgentIdentity? GenerationPersona = null,
    DateTimeOffset? CheckpointAtUtc = null,
    IReadOnlyList<Guid>? DerivedFromExperienceIds = null,
    string? MaintenanceOrigin = null);

public sealed record IdentityMaintenanceSettings(Guid AgentInstanceId, bool AllowAgentConsolidation, long Revision);

public static class IdentityMaintenanceLimits
{
    public const int MinSources = 2;
    public const int MaxSources = 8;
}
