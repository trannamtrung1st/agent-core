using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;

namespace AgentCore.Domain.Security;

public enum TenantApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }
public enum OperationGrantStatus { Available, Claimed, Consumed, Revoked, Indeterminate }

public enum TenantOperationKind { Persona, ConversationPreferences, CredentialConnectionCheck, SandboxCommand, SandboxControl, SandboxRead }
public sealed record PreparedConversationPreferences(string? ResponseLength, string? Language, bool? AskOneQuestionAtATime,
    bool ClearResponseLength = false, bool ClearLanguage = false, bool ClearAskOneQuestionAtATime = false);
public sealed record PreparedCredentialConnectionCheck(Guid CredentialId, Guid BindingId, string Reference,
    long BindingRevision, long CredentialRevision, long SecretRevision, long AuthEpoch);

public sealed record PreparedSandboxCommand(Guid SessionId, string ProfileId, long ProfileRevision, string CommandJson,
    long ExpectedLeaseRevision, Guid? ExpectedGeneration, Guid? RegistryCredentialId, long? RegistryCredentialRevision,
    long? RegistrySecretRevision, HomeAccess HomeAccess);

public enum SandboxControlKind { StopProcess, ResetEnvironment }
public sealed record PreparedSandboxControl(SandboxControlKind Kind, Guid SessionId, string ProfileId, long ProfileRevision,
    Guid Generation, Guid? ExecutionId, long ObservedSessionRevision, long ObservedLeaseRevision, long? ObservedExecutionRevision, string TargetHash);

public enum SandboxReadKind { Profiles, Status, Process }
public sealed record PreparedSandboxRead(SandboxReadKind Kind, Guid SessionId, Guid? ExecutionId, Guid? Generation,
    string ProfileReferencesJson, string TargetHash);

// Exactly one typed payload is allowed. No credential or bearer authority is part of this shape.
public sealed record PreparedTenantOperation(Guid OperationId, Guid InstanceId, AgentIdentity? Persona,
    long ExpectedInstanceRevision, long ExpectedPersonaRevision, string CanonicalActionJson, string ActionHash,
    PreparedConversationPreferences? Preferences = null, PreparedCredentialConnectionCheck? CredentialCheck = null, PreparedSandboxCommand? SandboxCommand = null, PreparedSandboxControl? SandboxControl = null, PreparedSandboxRead? SandboxRead = null)
{
    [JsonIgnore]
    public TenantOperationKind Kind => (Persona, Preferences, CredentialCheck, SandboxCommand, SandboxControl, SandboxRead) switch {
        (not null, null, null, null, null, null) => TenantOperationKind.Persona,
        (null, not null, null, null, null, null) => TenantOperationKind.ConversationPreferences,
        (null, null, not null, null, null, null) => TenantOperationKind.CredentialConnectionCheck,
        (null, null, null, not null, null, null) => TenantOperationKind.SandboxCommand,
        (null, null, null, null, not null, null) => TenantOperationKind.SandboxControl,
        (null, null, null, null, null, not null) => TenantOperationKind.SandboxRead,
        _ => throw new ArgumentException("Exactly one supported operation payload is required.") };
    [JsonIgnore] public bool IsLocalConfiguration => Kind is TenantOperationKind.Persona or TenantOperationKind.ConversationPreferences;
    [JsonIgnore] public string Permission => Kind == TenantOperationKind.SandboxRead ? "sandbox.read" : Kind == TenantOperationKind.SandboxControl ? "sandbox.manage" : Kind == TenantOperationKind.SandboxCommand ? "sandbox.execute" : IsLocalConfiguration ? "agents.update" : "credentials.use";
    [JsonIgnore] public string ResourceType => Kind != TenantOperationKind.CredentialConnectionCheck ? "agent" : "credential";
    [JsonIgnore] public string ResourceId => (Kind != TenantOperationKind.CredentialConnectionCheck ? InstanceId : CredentialCheck!.CredentialId).ToString("D");

    // Root correlation excludes mutable witnesses; grants still bind ActionHash exactly.
    [JsonIgnore]
    public string SemanticActionJson => Kind switch
    {
        TenantOperationKind.Persona => JsonSerializer.Serialize(new { operation = "agents.persona.update", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId,
            persona = new { Persona!.Name, Persona.Role, Persona.Description, Persona.Tone } }),
        TenantOperationKind.ConversationPreferences => JsonSerializer.Serialize(new { operation = "agents.preferences.update", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, preferences = Preferences }),
        TenantOperationKind.CredentialConnectionCheck => JsonSerializer.Serialize(new { operation = "credentials.connection.check", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId }),
        TenantOperationKind.SandboxCommand => JsonSerializer.Serialize(new { operation = "sandbox.command.execute", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, sessionId = SandboxCommand!.SessionId,
            profileId = SandboxCommand.ProfileId, commandJson = SandboxCommand.CommandJson }),
        TenantOperationKind.SandboxControl => JsonSerializer.Serialize(new { operation = "sandbox.control", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, SandboxControl!.Kind, SandboxControl.SessionId,
            SandboxControl.Generation, SandboxControl.ExecutionId, SandboxControl.TargetHash }),
        TenantOperationKind.SandboxRead => JsonSerializer.Serialize(new { operation = "sandbox.read", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, read = SandboxRead }),
        _ => throw new ArgumentException("Exactly one supported operation payload is required.")
    };
    [JsonIgnore]
    public string OperationFingerprint => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SemanticActionJson)));
}

public sealed record TenantApprovalRequest(Guid RequestId, Guid TenantId, Guid RunId, Guid SessionId,
    Guid RootActivationId, Guid ExecutorPrincipalId, Guid? OriginalInitiatorPrincipalId,
    ApprovalKind Kind, PreparedTenantOperation Operation, TenantApprovalStatus Status,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, TenantApprovalSnapshot? Snapshot = null)
{
    public string Permission => Operation.Permission;
    public string ResourceType => Operation.ResourceType;
    public string ResourceId => Operation.ResourceId;
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
