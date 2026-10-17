using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Security;

public enum TenantApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }
public enum OperationGrantStatus { Available, Claimed, Consumed, Revoked, Indeterminate }

public enum TenantLocalOperationKind { Persona, ConversationPreferences }
public sealed record PreparedConversationPreferences(string? ResponseLength, string? Language, bool? AskOneQuestionAtATime,
    bool ClearResponseLength = false, bool ClearLanguage = false, bool ClearAskOneQuestionAtATime = false);

// Exactly one typed payload is allowed. No credential or bearer authority is part of this shape.
public sealed record PreparedTenantOperation(Guid OperationId, Guid InstanceId, AgentIdentity? Persona,
    long ExpectedInstanceRevision, long ExpectedPersonaRevision, string CanonicalActionJson, string ActionHash,
    PreparedConversationPreferences? Preferences = null)
{
    [JsonIgnore]
    public TenantLocalOperationKind Kind => Preferences is null ? TenantLocalOperationKind.Persona : TenantLocalOperationKind.ConversationPreferences;

    // Root correlation excludes mutable witnesses; grants still bind ActionHash exactly.
    [JsonIgnore]
    public string SemanticActionJson => (Persona, Preferences) switch
    {
        ({ } persona, null) => JsonSerializer.Serialize(new { operation = "agents.persona.update", permission = TenantApprovalRequest.Permission,
            resourceType = TenantApprovalRequest.ResourceType, resourceId = InstanceId.ToString("D"),
            persona = new { persona.Name, persona.Role, persona.Description, persona.Tone } }),
        (null, { } preferences) => JsonSerializer.Serialize(new { operation = "agents.preferences.update", permission = TenantApprovalRequest.Permission,
            resourceType = TenantApprovalRequest.ResourceType, resourceId = InstanceId.ToString("D"), preferences }),
        _ => throw new ArgumentException("Exactly one supported operation payload is required.")
    };
    [JsonIgnore]
    public string OperationFingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SemanticActionJson)));
}

public sealed record TenantApprovalRequest(Guid RequestId, Guid TenantId, Guid RunId, Guid SessionId,
    Guid RootActivationId, Guid ExecutorPrincipalId, Guid? OriginalInitiatorPrincipalId,
    ApprovalKind Kind, PreparedTenantOperation Operation, TenantApprovalStatus Status,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    public const string Permission = "agents.update";
    public const string ResourceType = "agent";
    public string ResourceId => Operation.InstanceId.ToString("D");
    public string Fingerprint => Operation.OperationFingerprint;

    public TenantApprovalRequest Decide(TenantApprovalStatus decision, long expectedRevision, DateTimeOffset at)
    {
        if (Status != TenantApprovalStatus.Pending || Revision != expectedRevision)
            throw new InvalidOperationException("Approval decision is stale or already terminal.");
        if (decision is not (TenantApprovalStatus.Approved or TenantApprovalStatus.Rejected or TenantApprovalStatus.Cancelled or TenantApprovalStatus.Expired)
            || at.Offset != TimeSpan.Zero || at < CreatedAt || decision == TenantApprovalStatus.Expired != (at >= ExpiresAt))
            throw new ArgumentException("Approval decision or time is invalid.");
        return this with { Status = decision, Revision = checked(Revision + 1) };
    }
}

public sealed record TenantApprovalDecision(Guid DecisionId, Guid RequestId, Guid TenantId, Guid HumanPrincipalId,
    TenantApprovalStatus Status, string ActionHash, long RequestRevision, DateTimeOffset DecidedAt);

// The immutable request binds exact executor, permission, target, hash and witnesses. Never a RoleAssignment.
public sealed record TenantOperationGrant(Guid GrantId, Guid RequestId, Guid TenantId, Guid ExecutorPrincipalId,
    Guid OperationId, string ActionHash, string EffectIdempotencyKey, OperationGrantStatus Status, long Revision, DateTimeOffset ExpiresAt);

public sealed record ApprovalProcessedSignal(Guid SignalId, Guid TenantId, Guid RequestId, Guid RunId,
    long DecisionRevision, long Revision, DateTimeOffset CreatedAt, DateTimeOffset? ProcessedAt);
