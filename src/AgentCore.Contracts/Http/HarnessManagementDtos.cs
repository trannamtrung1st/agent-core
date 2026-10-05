namespace AgentCore.Contracts.Http;

public sealed record HarnessPolicyRequest(long ExpectedRevision, string Mode, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Sources, IReadOnlyList<string> EligibleTools, bool Frozen = false);
public sealed record HarnessStartRequest(long ExpectedRevision, string Purpose);
public sealed record HarnessRunRequest(string PreparationId);
public sealed record HarnessRevisionRequest(long ExpectedRevision);
public sealed record HarnessPromotionRequest(long ExpectedRevision, long DraftRevision);
public sealed record HarnessApprovalRequest(long ExpectedRevision, string ActionHash, bool Approve);
public sealed record HarnessPolicyResponse(string Mode, IReadOnlyList<string> Scopes, IReadOnlyList<string> Sources,
    IReadOnlyList<string> EligibleTools, bool Frozen);
public sealed record HarnessSkillResponse(string Id, string Name, string Description, string Procedure,
    IReadOnlyList<string> RequiredCapabilities, IReadOnlyList<string> ResourcePaths);
public sealed record HarnessOperationResponse(string Kind, long DraftRevision, string? Id, string? Content,
    string? Source, HarnessSkillResponse? Skill, bool? Enabled, bool? AllowUnreadUnsupportedTypes);
public sealed record HarnessApprovalResponse(string ApprovalId, string ActionHash, HarnessOperationResponse Operation, string Status);
public sealed record HarnessEvidenceResponse(string Actor, long DraftRevision, string Check, string Status,
    string Expected, string Observed, string? Limitation);
public sealed record HarnessPreparationResponse(string PreparationId, string DraftId, int BaseVersion, string Purpose,
    string Status, IReadOnlyList<HarnessApprovalResponse> Approvals, IReadOnlyList<HarnessEvidenceResponse> Evidence,
    int? PublishedVersion, string? DiagnosticId, long? PublishedDraftRevision);
public sealed record HarnessKnowledgeResponse(string Identity, string Title, string Citation, string? ResourcePath);
public sealed record HarnessReviewResponse(string InstanceId, long InstanceRevision, int ActiveVersion,
    HarnessPolicyResponse Policy, long PolicyRevision, HarnessPreparationResponse? Preparation,
    long? DraftRevision, string? Instructions, IReadOnlyList<HarnessSkillResponse> Skills,
    IReadOnlyList<HarnessKnowledgeResponse> Knowledge, IReadOnlyList<string> SelectedTools,
    AdminDefinitionDraftDiffResponse? Diff, IReadOnlyList<AdminDefinitionDraftResourceResponse> Resources);
