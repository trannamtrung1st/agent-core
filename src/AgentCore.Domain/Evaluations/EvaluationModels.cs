using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentCore.Domain.Evaluations;

public enum EvaluatorKind { Script, Agent, Manual }
public enum ScriptKind { Assertion, Sandbox }
public enum EvaluationHook { BeforeRun, AfterRun, OnFailure, Manual, Publication }
public enum EvaluationPolicy { RecordOnly, RequirePassBeforeRun, RequirePassForVerifiedOutcome, RequirePassBeforePublication, RequestManualAssessment }
public enum EvaluationOutcome { Passed, Failed, Inconclusive }
public enum EvaluationStatus { Pending, Running, Completed, Cancelled, Indeterminate }
public enum EvaluationSubjectKind { DefinitionDraft, AgentRun }
public enum EvaluationPredicate { ToolOffered, ToolNotOffered, ResourceBound, TriggerSchedulePermitted, ExternalActionDenied, RunCompleted, OutputPresent, OutcomeSucceeded, InputPresent }

public sealed record EvaluationBinding(Guid SuiteId, int Version, EvaluationHook Hook, EvaluationPolicy Policy = EvaluationPolicy.RecordOnly);
public sealed record ScriptReference(Guid ScriptId, int Version, string ContentHash);
public sealed record EvaluationCheck(string CheckId, string Title, EvaluatorKind Kind, bool Required,
    EvaluationPredicate Predicate = EvaluationPredicate.OutputPresent, string? Expected = null, ScriptReference? Script = null,
    string? Rubric = null, Guid? ModelOfferingId = null, string? Prompt = null);
public sealed record EvaluationSuiteVersion(Guid TenantId, Guid SuiteId, int Version, string Name,
    IReadOnlyList<EvaluationCheck> Checks, string ContentHash, Guid CreatedBy, DateTimeOffset CreatedAt,
    Guid? DraftId = null, bool Archived = false, Guid? DefinitionId = null);
public sealed record ScriptVersion(Guid TenantId, Guid ScriptId, int Version, string Name, ScriptKind Kind,
    EvaluationPredicate Predicate, string? Expected, string? Body, IReadOnlyList<EvaluationHook> Hooks,
    string ContentHash, Guid CreatedBy, DateTimeOffset CreatedAt, bool Archived = false);
public sealed record EvaluationFinding(string CheckId, EvaluatorKind Kind, bool Required, EvaluationOutcome Outcome,
    string ReasonCode, string Summary, Guid? ReviewerId = null, DateTimeOffset? ReviewedAt = null,
    string? ModelIdentity = null);
public sealed record EvaluationAttempt(Guid AttemptId, Guid TenantId, EvaluationSubjectKind SubjectKind, Guid SubjectId,
    long SubjectRevision, string SourceFingerprint, Guid SuiteId, int SuiteVersion, string SuiteHash,
    EvaluationHook Hook, Guid ActorId, string DedupKey, EvaluationStatus Status, EvaluationOutcome? Outcome,
    IReadOnlyList<EvaluationFinding> Findings, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    long Revision = 1, long? CommitSequence = null, string? ReasonCode = null, IReadOnlyList<EvaluationModelPin>? ModelPins = null, Guid? SourceDefinitionId = null);
public sealed record EvaluationModelPin(string CheckId, AgentCore.Domain.Models.ModelBinding Binding);

public static class EvaluationRules
{
    public const int MaxChecks = 100;
    public const int MaxVersions = 1000;
    public const int MaxCatalogs = 200;
    public const int MaxFindingsCharacters = 2000;
    public const int TimeoutSeconds = 10;
    public const int ModelTokenLimit = 512;
    public const int MaxConcurrent = 2;
    public static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    public static EvaluationOutcome Aggregate(IEnumerable<EvaluationFinding> findings)
    {
        var required = findings.Where(f => f.Required).ToArray();
        return required.Any(f => f.Outcome == EvaluationOutcome.Failed) ? EvaluationOutcome.Failed
            : required.Length == 0 || required.Any(f => f.Outcome != EvaluationOutcome.Passed) ? EvaluationOutcome.Inconclusive : EvaluationOutcome.Passed;
    }
    public static EvaluationSuiteVersion Freeze(EvaluationSuiteVersion suite)
    {
        Identity(suite.TenantId, suite.SuiteId, suite.Version, suite.Name);
        if (suite.Checks is null || suite.Checks.Count > MaxChecks || suite.Checks.Any(c => c is null) || suite.Checks.Select(c => c.CheckId).Distinct(StringComparer.Ordinal).Count() != suite.Checks.Count)
            throw new ArgumentException("Use at most 100 uniquely named checks.");
        if (suite.Checks.Count(c => c.Kind == EvaluatorKind.Agent) > 4) throw new ArgumentException("Use at most four bounded model checks per Suite.");
        foreach (var check in suite.Checks)
        {
            if (string.IsNullOrWhiteSpace(check.CheckId) || check.CheckId.Length > 64 || !check.CheckId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
                || string.IsNullOrWhiteSpace(check.Title) || check.Title.Length > 128 || !Enum.IsDefined(check.Kind) || !Enum.IsDefined(check.Predicate)
                || check.Expected?.Length > 256 || check.Rubric?.Length > 2000 || check.Prompt?.Length > 2000
                || check.Kind == EvaluatorKind.Agent && string.IsNullOrWhiteSpace(check.Rubric)
                || check.Script is { } reference && (check.Kind != EvaluatorKind.Script || reference.ScriptId == Guid.Empty || reference.Version < 1 || reference.ContentHash is not { Length: 64 } || reference.ContentHash.Any(c => !char.IsAsciiHexDigit(c))))
                throw new ArgumentException("Invalid evaluation check.");
        }
        var checks = Array.AsReadOnly(suite.Checks.ToArray());
        return suite with { Checks = checks, ContentHash = Hash(checks) };
    }
    public static ScriptVersion Freeze(ScriptVersion script)
    {
        Identity(script.TenantId, script.ScriptId, script.Version, script.Name);
        if (!Enum.IsDefined(script.Kind) || !Enum.IsDefined(script.Predicate) || script.Expected?.Length > 256
            || script.Hooks is null || script.Hooks.Count is < 1 or > 4 || script.Hooks.Any(h => !Enum.IsDefined(h) || h == EvaluationHook.Publication)
            || script.Hooks.Distinct().Count() != script.Hooks.Count || script.Body?.Length > 16_000
            || script.Kind == ScriptKind.Sandbox && string.IsNullOrWhiteSpace(script.Body)
            || script.Kind == ScriptKind.Assertion && script.Body is not null)
            throw new ArgumentException("Invalid Script kind, content or hooks.");
        var hooks = Array.AsReadOnly(script.Hooks.Order().ToArray());
        return script with { Hooks = hooks, ContentHash = Hash(new { script.Kind, script.Predicate, script.Expected, script.Body, Hooks = hooks }) };
    }
    public static void ValidateBindings(IReadOnlyList<EvaluationBinding>? bindings)
    {
        if (bindings is null) return;
        if (bindings.Count > 8 || bindings.Any(b => b is null) || bindings.Distinct().Count() != bindings.Count) throw new ArgumentException("Use at most eight distinct evaluation bindings.");
        foreach (var b in bindings)
            if (b.SuiteId == Guid.Empty || b.Version < 1 || !Enum.IsDefined(b.Hook) || !Enum.IsDefined(b.Policy)
                || b.Policy == EvaluationPolicy.RequirePassBeforeRun && b.Hook != EvaluationHook.BeforeRun
                || b.Policy == EvaluationPolicy.RequirePassBeforePublication && b.Hook != EvaluationHook.Publication
                || b.Policy is EvaluationPolicy.RequirePassForVerifiedOutcome or EvaluationPolicy.RequestManualAssessment && b.Hook is not (EvaluationHook.AfterRun or EvaluationHook.OnFailure or EvaluationHook.Manual))
                throw new ArgumentException("Evaluation policy does not match its hook.");
    }
    public static void ValidatePolicy(EvaluationBinding binding, EvaluationSuiteVersion suite)
    {
        if (binding.Policy == EvaluationPolicy.RequestManualAssessment && !suite.Checks.Any(c => c.Kind == EvaluatorKind.Manual))
            throw new ArgumentException("Request human assessment requires a Manual check in the exact Suite version.");
    }
    private static void Identity(Guid tenant, Guid id, int version, string name)
    {
        if (tenant == Guid.Empty || id == Guid.Empty || version is < 1 or > MaxVersions || string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("Invalid evaluation identity, version or name.");
    }
}

public sealed record EvaluationGovernance(Guid TenantId, long Revision, IReadOnlyList<EvaluationBinding> Defaults);
