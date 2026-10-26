using AgentCore.Application.Ports;

namespace AgentCore.Application.Decisions;

public enum DecisionKind { Choice, YesNo, Score }
public enum DecisionPurpose { ModelRouting, ReasoningEffort, SkillRelevance, ContextRelevance, InitiativeRelevance, EventRelevance, ConfigurationAdvice, WorkPriority }
public enum DecisionStatus { Ready, Disabled, NotConfigured, Starting, Unavailable, Timeout, Saturated, InvalidRequest, InvalidResult, LowConfidence, UnsupportedScope, StaleAuthority }

public sealed record DecisionCandidate(string Id, string Label, string? Description = null);

// Only an existing authoritative owner can supply an invocation. Scope is never serialized to a local backend.
public sealed record DecisionScope(ModelInvocation? Invocation = null);
public sealed record DecisionRequest(DecisionPurpose Purpose, DecisionKind Kind,
    IReadOnlyList<DecisionCandidate> Candidates, IReadOnlyDictionary<string, string> Signals,
    DecisionScope Scope, string PolicyRevision, double MinimumProbability = 0.7);
public sealed record DecisionResult(DecisionStatus Status, string? SelectedCandidateId,
    IReadOnlyDictionary<string, double> Probabilities, double? Score, string EngineId, string EngineVersion,
    string? DiagnosticCode = null)
{
    public static DecisionResult Failure(DecisionStatus status, string engine, string version, string? code = null) =>
        new(status, null, new Dictionary<string, double>(), null, engine, version, code ?? status.ToString());
}

public interface IDecisionEngine
{
    // Local host policy, no readiness I/O. Admission snapshots it once for both decision stages.
    DecisionExecutionPolicy Policy { get; }
    ValueTask<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default);
    ValueTask<DecisionEngineStatus> StatusAsync(CancellationToken ct = default);
}

public sealed record DecisionExecutionPolicy(bool ShadowMode, int AdmissionTimeoutMs, string EngineId = "decision", string EngineVersion = "unknown");

public sealed record DecisionEngineStatus(DecisionStatus Status, string EngineId, string EngineVersion, bool PreAdmissionSupported, bool ShadowMode);

// Transport implementations return untrusted results; the shared engine always validates them.
public interface IDecisionBackend
{
    string Id { get; }
    string Version { get; }
    bool PreAdmissionSupported { get; }
    ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct);
    ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct);
}
