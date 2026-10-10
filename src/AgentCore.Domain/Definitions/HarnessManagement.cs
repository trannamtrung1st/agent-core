namespace AgentCore.Domain.Definitions;

public enum HarnessManagementMode { Disabled, Assisted, Managed }
public enum HarnessManagementScope { KnowledgeResources, Instructions, ToolSelection }
public enum HarnessPreparationStatus { Preparing, AwaitingApproval, Ready, Failed, Cancelled, Published }
public enum HarnessEvidenceStatus { Verified, PartiallyVerified, CannotVerify, RequiresExternalEvidence, Failed }

public sealed record HarnessManagementPolicy(
    HarnessManagementMode Mode,
    IReadOnlyList<HarnessManagementScope> Scopes,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> EligibleTools,
    bool Frozen = false)
{
    public static HarnessManagementPolicy Disabled { get; } = new(HarnessManagementMode.Disabled, [], [], []);
    public bool Allows(HarnessManagementScope scope) => !Frozen && Mode != HarnessManagementMode.Disabled && Scopes.Contains(scope);
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
    IReadOnlyList<HarnessInstanceChange>? InstanceChanges = null);

public sealed record HarnessInstanceChange(Guid OperationId, string Operation, long InstanceRevision,
    int DefinitionVersion, long PolicyRevision, Guid? ApprovalId, string ActionHash, string? Source,
    DateTimeOffset SavedAt, HarnessVerificationEvidence Evidence);

public sealed record HarnessPublishedChange(string SectionId, string Label, string ChangeKind, string? BeforeSummary, string? AfterSummary);
