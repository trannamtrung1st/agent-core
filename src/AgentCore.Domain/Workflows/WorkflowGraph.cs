using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.Domain.Workflows;

public enum WorkflowNodeKind { Start, AgentTask, Verify, HumanDecision, Branch, EndSuccess, EndFailure }
public enum WorkflowRequirement { None, Global, Branch }
public enum WorkflowInvocationKind { HumanManual, ClaimedAgent, AutomationOccurrence }
public enum WorkflowInputKind { Text, Boolean, Number, Date, Choice }
public enum WorkflowVerificationKind { Evaluation, GoalCheckpoint }
public enum WorkflowFactKind { VerificationPassed, DecisionApproved, TaskOutcome }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowInput(string Key, string Label, WorkflowInputKind Kind, bool Required,
    int MaxLength = 1000, IReadOnlyList<string>? Choices = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowAgentTask(Guid TargetInstanceId, string Objective, Guid SpaceId, Guid? EnvironmentId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowVerification(WorkflowVerificationKind Kind, string? TaskNodeId = null,
    Guid? SuiteId = null, int? SuiteVersion = null, string? SuiteHash = null,
    Guid? SessionId = null, Guid? GoalId = null, Guid? CheckpointId = null, long? PlanRevision = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowHumanDecision(Guid ReviewerPrincipalId, string Prompt, int TimeoutHours = 72);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowGoalSuccess(Guid SessionId, Guid GoalId, long PlanRevision);

/// <summary>Closed facts only. Neither model output nor input is an expression or resource selector.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowBranch(string SourceNodeId, WorkflowFactKind Fact);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowNode(string Id, string Name, WorkflowNodeKind Kind,
    WorkflowRequirement Requirement = WorkflowRequirement.None, string? RequiredOnEdgeId = null,
    WorkflowAgentTask? Task = null, WorkflowVerification? Verification = null,
    WorkflowHumanDecision? Decision = null, WorkflowBranch? Branch = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowEdge(string Id, string From, string To, string Outcome = "next");

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkflowGraph(int SchemaVersion, IReadOnlyList<WorkflowNode> Nodes,
    IReadOnlyList<WorkflowEdge> Edges, IReadOnlyList<WorkflowInput> Inputs,
    IReadOnlyList<WorkflowInvocationKind> AllowedOrigins, IReadOnlyList<Guid> CoordinatorInstanceIds,
    int MaxTotalTaskSteps = 144, int MaxTotalTaskSeconds = 900,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowGoalSuccess? SuccessGoal = null);

public sealed record WorkflowDiagnostic(string Code, string Message, string? NodeId = null, string? EdgeId = null);

/// <summary>Immutable persisted representation; callers get a fresh graph instead of mutable version state.</summary>
public sealed record PublishedWorkflowGraph
{
    public PublishedWorkflowGraph(string canonicalJson, string hash)
    {
        var validated = WorkflowGraphRules.ValidateAndFreeze(WorkflowGraphRules.Parse(canonicalJson));
        if (validated.CanonicalJson != canonicalJson || validated.Hash != hash)
            throw new ArgumentException("Published graph does not match its canonical hash.");
        CanonicalJson = canonicalJson; Hash = hash;
    }
    internal PublishedWorkflowGraph(string canonicalJson, string hash, bool validated)
    {
        CanonicalJson = canonicalJson; Hash = hash;
    }
    public string CanonicalJson { get; }
    public string Hash { get; }
    public WorkflowGraph Read() => WorkflowGraphRules.Parse(CanonicalJson);
}

public static partial class WorkflowGraphRules
{
    public const int MaxNodes = 40, MaxEdges = 80, MaxInputs = 16, MaxJsonBytes = 96 * 1024;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false, AllowDuplicateProperties = false, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static WorkflowGraph Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new ArgumentException("Workflow graph is too large.");
        try { return JsonSerializer.Deserialize<WorkflowGraph>(json, Json) ?? throw new ArgumentException("A graph is required."); }
        catch (JsonException error) { throw new ArgumentException("Use the closed workflow graph schema with unique fields.", error); }
    }
    public static PublishedWorkflowGraph ValidateAndFreeze(WorkflowGraph graph)
    {
        var errors = Validate(graph);
        if (errors.Count != 0) throw new ArgumentException(string.Join("; ", errors.Select(e => $"{e.Code}:{e.NodeId ?? e.EdgeId}: {e.Message}")));
        var normalized = graph with {
            Nodes = graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToArray(),
            Edges = graph.Edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToArray(),
            Inputs = graph.Inputs.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => i with {
                Choices = i.Choices?.Order(StringComparer.Ordinal).ToArray() }).ToArray(),
            AllowedOrigins = graph.AllowedOrigins.Order().ToArray(), CoordinatorInstanceIds = graph.CoordinatorInstanceIds.Order().ToArray()
        };
        var json = JsonSerializer.Serialize(normalized, Json);
        if (Encoding.UTF8.GetByteCount(json) > MaxJsonBytes) throw new ArgumentException("Workflow graph is too large.");
        return new(json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), true);
    }
    internal static bool Key(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');
    internal static bool Text(string? value, int max)
    {
        if (value is not { Length: > 0 } || value.Length > max || value != value.Trim()
            || value.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    internal static bool Id(Guid? value) => value is { } id && id != Guid.Empty;
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
