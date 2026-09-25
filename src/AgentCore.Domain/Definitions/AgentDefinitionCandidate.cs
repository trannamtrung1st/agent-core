namespace AgentCore.Domain.Definitions;

/// <summary>
/// Mutable definition payload for durable Admin drafts. Published versions are materialized
/// as <see cref="AgentDefinition"/> with a server-assigned version.
/// </summary>
public sealed record AgentDefinitionCandidate(
    int SchemaVersion,
    string DefinitionId,
    AgentIdentity Identity,
    IReadOnlyList<string> Goals,
    string SystemInstructions,
    BehaviorPolicy BehaviorPolicy,
    ConversationPolicy ConversationPolicy,
    InitiativePolicy InitiativePolicy,
    VoiceConfiguration Voice,
    ProviderPreferences ProviderPreferences,
    IReadOnlyDictionary<string, string> Metadata,
    RoleEnvironment? Environment = null,
    AgentModelDefaults? ModelDefaults = null,
    MemoryPolicy? MemoryPolicy = null,
    TriggerPolicy? TriggerPolicy = null)
{
    public AgentDefinition ToPublished(int version) =>
        new(
            SchemaVersion,
            DefinitionId,
            version,
            Identity,
            Goals,
            SystemInstructions,
            BehaviorPolicy,
            ConversationPolicy,
            InitiativePolicy,
            Voice,
            ProviderPreferences,
            Metadata,
            Environment,
            ModelDefaults,
            MemoryPolicy,
            TriggerPolicy);

    public static AgentDefinitionCandidate FromDefinition(AgentDefinition definition) =>
        new(
            definition.SchemaVersion,
            definition.Id,
            definition.Identity,
            definition.Goals,
            definition.SystemInstructions,
            definition.BehaviorPolicy,
            definition.ConversationPolicy,
            definition.InitiativePolicy,
            definition.Voice,
            definition.ProviderPreferences,
            definition.Metadata,
            definition.Environment,
            definition.ModelDefaults,
            definition.MemoryPolicy,
            definition.TriggerPolicy);
}
