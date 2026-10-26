using System.Text.Json.Serialization;

namespace AgentCore.Domain.Definitions;

/// <summary>
/// Mutable definition payload for durable Admin drafts. Published versions are materialized
/// as <see cref="AgentDefinition"/> with a server-assigned version.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentDefinitionCandidate(
    int SchemaVersion,
    string DefinitionId,
    AgentIdentity Identity,
    IReadOnlyList<string> Goals,
    string AgentInstructions,
    BehaviorPolicy BehaviorPolicy,
    ConversationPolicy ConversationPolicy,
    InitiativePolicy InitiativePolicy,
    VoiceConfiguration Voice,
    ProviderPreferences ProviderPreferences,
    IReadOnlyDictionary<string, string> Metadata,
    DefinitionExecutionPolicy? ExecutionPolicy = null,
    AgentCore.Domain.Models.ModelPreference? ModelDefaults = null,
    MemoryPolicy? MemoryPolicy = null,
    TriggerPolicy? TriggerPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<SkillSpec>? Skills = null,
    ExecutionBudgetPolicy? ExecutionBudgets = null,
    HarnessManagementPolicy? SelfManagement = null,
    SandboxPolicy? SandboxPolicy = null,
    StateIsolationPolicy? StateIsolationPolicy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<AgentCore.Domain.Evaluations.EvaluationBinding>? EvaluationBindings = null)
{
    public IReadOnlyList<SkillSpec> SkillList => Skills ?? [];

    public AgentDefinition ToPublished(int version) =>
        new(
            SchemaVersion,
            DefinitionId,
            version,
            Identity,
            Goals,
            AgentInstructions,
            BehaviorPolicy,
            ConversationPolicy,
            InitiativePolicy,
            Voice,
            ProviderPreferences,
            Metadata,
            ExecutionPolicy,
            ModelDefaults,
            MemoryPolicy,
            TriggerPolicy,
            Skills, ExecutionBudgets, SelfManagement: SelfManagement, SandboxPolicy: SandboxPolicy, StateIsolationPolicy: StateIsolationPolicy, EvaluationBindings: EvaluationBindings);

    public static AgentDefinitionCandidate FromDefinition(AgentDefinition definition) =>
        new(
            definition.SchemaVersion,
            definition.Id,
            definition.Identity,
            definition.Goals,
            definition.AgentInstructions,
            definition.BehaviorPolicy,
            definition.ConversationPolicy,
            definition.InitiativePolicy,
            definition.Voice,
            definition.ProviderPreferences,
            definition.Metadata,
            definition.ExecutionPolicy,
            definition.ModelDefaults,
            definition.MemoryPolicy,
            definition.TriggerPolicy,
            definition.Skills, definition.ExecutionBudgets, definition.SelfManagement, definition.SandboxPolicy, definition.StateIsolationPolicy, definition.EvaluationBindings);
}
