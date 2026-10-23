using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Admin;

public static class AdminDefinitionSources
{
    public const string BuiltIn = "builtIn";
    public const string Durable = "durable";
    public const string Draft = "draft";
}

public static class AdminDefinitionStatuses
{
    public const string Published = "published";
    public const string Deprecated = "deprecated";
    public const string DraftOnly = "draftOnly";
}

public sealed record AdminDefinitionInventoryItem(
    string DefinitionId,
    int Version,
    string Source,
    string Status,
    string DisplayName,
    int DraftCount = 0);

public sealed record AdminInstanceInventoryItem(
    Guid InstanceId,
    string DefinitionId,
    int ActiveVersion,
    AgentInstanceLifecycle Lifecycle,
    string PersonaName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AdminKnowledgeSource(
    string Identity,
    string Title,
    string Citation,
    string ResolvedResourcePath);

public sealed record AdminEffectiveModel(
    string CatalogKey,
    string DisplayName,
    string SelectionSource,
    string? ReasoningEffort,
    string? ModelId);

public sealed record AdminDurableExecutionEligibility(
    bool InstanceActive,
    bool DefinitionResolved,
    bool TriggerPolicyEnabled,
    bool AllowsScheduleSource,
    bool AllowsApplicationEventSource,
    bool CanAcceptNewTriggeredWork);

public sealed record AdminEffectiveConfiguration(
    string DefinitionSource,
    string DefinitionId,
    int DefinitionVersion,
    string DefinitionStatus,
    Guid InstanceId,
    string InstanceLifecycle,
    long InstanceRevision,
    long PersonaRevision,
    AgentIdentity Persona,
    ProviderPreferences ProviderPreferences,
    AdminEffectiveModel EffectiveModel,
    IReadOnlyList<string> EffectiveToolAllowlist,
    IReadOnlyList<string> HarnessReferences,
    string? WorkspaceTemplateId,
    IReadOnlyList<AdminKnowledgeSource> KnowledgeSources,
    MemoryPolicy MemoryPolicy,
    TriggerPolicy? TriggerPolicy,
    AdminDurableExecutionEligibility DurableExecutionEligibility,
    string? UnattendedModelCatalogKey = null,
    string? UnattendedReasoningEffort = null,
    BrowserEffectiveConfiguration? Browser = null);

public sealed record BrowserEffectiveConfiguration(string ProviderId, string DisplayName, bool Enabled, bool Ready, string ProfileMode, string PolicyMode, IReadOnlyList<string> SupportedFeatures, int MaxSnapshotBytes, int MaxCaptureBytes, int MaxDownloadBytes, string Engine = "unknown", BrowserOperationalLimits? Limits = null);
