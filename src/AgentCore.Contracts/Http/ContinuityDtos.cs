using System.Text.Json.Serialization;

namespace AgentCore.Contracts.Http;

public sealed record ExperienceConfigurationRequest(long ExpectedRevision, bool Enabled);
public sealed record ExperienceCheckpointRequest(string SessionId);
public sealed record ExperienceVisibilityRequest(long ExpectedRevision, string Visibility);
public sealed record ExperienceContentResponse(string Goal, IReadOnlyList<string> Attempts, IReadOnlyList<string> Decisions,
    IReadOnlyList<string> Outcomes, IReadOnlyList<string> Corrections, IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> Difficulties, IReadOnlyList<string> Lessons);
public sealed record ExperienceResponse(string ExperienceId, string SourceKind, string SourceId, long ThroughCursor,
    string SourceAt, string DefinitionId, int DefinitionVersion, string GenerationWorkItemId, string ModelKey,
    string Status, string Visibility, long Revision, bool EligibleForContext, ExperienceContentResponse? Content,
    string? DiagnosticId, string? FailureSummary, string? SourceCreatedAt = null, string? CheckpointAt = null,
    IReadOnlyList<string>? DerivedFromExperienceIds = null, string? MaintenanceOrigin = null);
public sealed record ExperienceReviewResponse(bool Enabled, long SettingsRevision, int ContextBudgetCharacters,
    IReadOnlyList<ExperienceResponse> Items);
public sealed record ThoughtRegistrationRequest(long ExpectedRevision, bool Enabled, int IntervalSeconds,
    string ThinkingPrompt, string? ModelKey = null, string? ReasoningEffort = null);
public sealed record ContinuityRevisionRequest(long ExpectedRevision);
public sealed record ThoughtRegistrationResponse(string RegistrationId, long Revision, bool Enabled, string Status,
    int IntervalSeconds, string ThinkingPrompt, string? ModelKey, string? ReasoningEffort,
    string? NextRunAt, string? LastRunAt, string? LastOutcome, string? LastWorkItemId, string? ExecutionStatus,
    string? EffectiveModelKey);
public sealed record ThoughtReviewResponse(int MinIntervalSeconds, IReadOnlyList<ThoughtRegistrationResponse> Items);

public sealed record IdentityMaintenanceConfigurationRequest(long ExpectedRevision, bool AllowAgentConsolidation);

public sealed record ContinuityMaintenanceConfigurationRequest(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] int? IntervalSeconds);
public sealed record ContinuityMaintenanceResponse(int? ConfiguredIntervalSeconds, int EffectiveIntervalSeconds,
    int MinimumIntervalSeconds, int MaximumIntervalSeconds, int DefaultIntervalSeconds, bool UsesDefault,
    bool ConfiguredIntervalAllowed, long Revision, string? LastMaintenanceAtUtc);
