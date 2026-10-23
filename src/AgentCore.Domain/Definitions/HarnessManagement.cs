namespace AgentCore.Domain.Definitions;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HarnessManagementMode>))]
public enum HarnessManagementMode { Disabled, Assisted, Managed }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<HarnessManagementScope>))]
public enum HarnessManagementScope { KnowledgeResources, Instructions, ToolSelection, Settings, Skills, Resources, Connections, ManagementPolicy, AutomationPreferences }
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<ManagementAutoApply>))]
public enum ManagementAutoApply { ConversationPreferences, TextResources, SkillChanges }
public sealed record ManagementSkillChoices(bool ConfigurationAssistance = true, bool HarnessAuthoring = true);
/// <summary>Missing fields inherit the selected Definition. Freeze is an Instance-local lock.</summary>
public sealed record ManagementPolicyOverrides(
    InstanceField<HarnessManagementMode>? Mode = null,
    InstanceField<HarnessManagementScope[]>? Scopes = null,
    InstanceField<ManagementAutoApply[]>? AutoApply = null,
    InstanceField<bool>? ConfigurationAssistance = null,
    InstanceField<bool>? HarnessAuthoring = null)
{
    // Only legacy full-policy records need materialization. Fresh records remain sparse.
    public static ManagementPolicyOverrides FromLegacy(HarnessManagementPolicy policy) => new(
        new(policy.Mode), new(policy.Scopes.ToArray()), new((policy.ManagedAutoApply ?? []).ToArray()),
        new(policy.BuiltInSkills?.ConfigurationAssistance ?? true), new(policy.BuiltInSkills?.HarnessAuthoring ?? true));
}
// Inert historical serialization only. The preparation/approval executor is retired.
public enum HarnessPreparationStatus { Preparing, AwaitingApproval, Ready, Failed, Cancelled, Published }
public enum HarnessEvidenceStatus { Verified, PartiallyVerified, CannotVerify, RequiresExternalEvidence, Failed }

public sealed record HarnessManagementPolicy(
    HarnessManagementMode Mode,
    IReadOnlyList<HarnessManagementScope> Scopes,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> EligibleTools,
    bool Frozen = false,
    IReadOnlyList<ManagementAutoApply>? ManagedAutoApply = null,
    ManagementSkillChoices? BuiltInSkills = null)
{
    public static HarnessManagementPolicy Disabled { get; } = new(HarnessManagementMode.Disabled, [], [], []);
    public bool Allows(HarnessManagementScope scope) => !Frozen && Mode != HarnessManagementMode.Disabled && Scopes.Contains(scope);
    public bool AutoApplies(ManagementAutoApply operation) => !Frozen && Mode == HarnessManagementMode.Managed && (ManagedAutoApply ?? []).Contains(operation);
}

/// <summary>Semantic draft operation; never a patch to trusted configuration.</summary>
public sealed record HarnessAuthoringOperation(
    string Kind,
    long DraftRevision,
    string? Id = null,
    string? Content = null,
    string? Source = null,
    bool? Enabled = null,
    bool? AllowUnreadUnsupportedTypes = null);

public sealed record HarnessAuthoringApproval(
    Guid ApprovalId,
    string ActionHash,
    HarnessAuthoringOperation Operation,
    string Status);

public sealed record HarnessVerificationEvidence(
    string Actor,
    long DraftRevision,
    string Check,
    HarnessEvidenceStatus Status,
    string Expected,
    string Observed,
    string? Limitation = null);

public sealed record HarnessPreparation(
    Guid PreparationId,
    Guid DraftId,
    int BaseVersion,
    string Purpose,
    long PolicyRevision,
    HarnessPreparationStatus Status,
    IReadOnlyList<HarnessAuthoringApproval> Approvals,
    IReadOnlyList<HarnessVerificationEvidence> Evidence,
    int? PublishedVersion = null,
    Guid? DiagnosticId = null,
    long? PublishedDraftRevision = null,
    IReadOnlyList<HarnessPublishedChange>? PublishedChanges = null,
    int StepsUsed = 0,
    int OutputBytes = 0,
    long RemainingOverallBudgetMs = 180000);

public sealed record HarnessManagementState(
    HarnessManagementPolicy Policy,
    long PolicyRevision = 1,
    HarnessPreparation? Preparation = null,
    IReadOnlyList<HarnessInstanceChange>? InstanceChanges = null,
    ManagementPolicyOverrides? Overrides = null);

public static class ManagementPolicyResolver
{
    // A live Run may lose delegated management, but a later policy edit cannot add
    // authority to its immutable admission. Settings and instructions stay pinned.
    public static HarnessManagementPolicy Restrict(HarnessManagementPolicy? admitted, HarnessManagementPolicy? current)
    {
        var pin = admitted ?? HarnessManagementPolicy.Disabled;
        var live = current ?? HarnessManagementPolicy.Disabled;
        return pin with {
            Mode = pin.Mode == HarnessManagementMode.Disabled || live.Mode == HarnessManagementMode.Disabled
                ? HarnessManagementMode.Disabled
                : pin.Mode == HarnessManagementMode.Managed && live.Mode == HarnessManagementMode.Managed
                    ? HarnessManagementMode.Managed : HarnessManagementMode.Assisted,
            Frozen = pin.Frozen || live.Frozen,
            Scopes = pin.Scopes.Intersect(live.Scopes).ToArray(),
            Sources = pin.Sources.Intersect(live.Sources, StringComparer.Ordinal).ToArray(),
            EligibleTools = pin.EligibleTools.Intersect(live.EligibleTools, StringComparer.Ordinal).ToArray(),
            ManagedAutoApply = (pin.ManagedAutoApply ?? []).Intersect(live.ManagedAutoApply ?? []).ToArray(),
            BuiltInSkills = new((pin.BuiltInSkills?.ConfigurationAssistance ?? true) && (live.BuiltInSkills?.ConfigurationAssistance ?? true),
                (pin.BuiltInSkills?.HarnessAuthoring ?? true) && (live.BuiltInSkills?.HarnessAuthoring ?? true))
        };
    }

    public static HarnessManagementPolicy Resolve(AgentDefinition definition, AgentInstance instance)
    {
        var defaults = definition.SelfManagement ?? HarnessManagementPolicy.Disabled;
        var state = instance.HarnessManagement;
        // Existing non-null records retain precisely their legacy scopes. Newly introduced areas are never inferred.
        if (state?.Overrides is not { } overrides) return state?.Policy ?? defaults with { Frozen = false };
        return defaults with {
            Mode = overrides.Mode?.Value ?? defaults.Mode,
            Scopes = overrides.Scopes?.Value ?? defaults.Scopes,
            ManagedAutoApply = overrides.AutoApply?.Value ?? defaults.ManagedAutoApply,
            BuiltInSkills = new(overrides.ConfigurationAssistance?.Value ?? defaults.BuiltInSkills?.ConfigurationAssistance ?? true,
                overrides.HarnessAuthoring?.Value ?? defaults.BuiltInSkills?.HarnessAuthoring ?? true),
            Frozen = state.Policy.Frozen,
            Sources = state.Policy.Sources,
            EligibleTools = state.Policy.EligibleTools
        };
    }
}

public sealed record HarnessInstanceChange(Guid OperationId, string Operation, long InstanceRevision,
    int DefinitionVersion, long PolicyRevision, Guid? ApprovalId, string ActionHash, string? Source,
    DateTimeOffset SavedAt, HarnessVerificationEvidence Evidence);

public sealed record HarnessPublishedChange(string SectionId, string Label, string ChangeKind, string? BeforeSummary, string? AfterSummary);
