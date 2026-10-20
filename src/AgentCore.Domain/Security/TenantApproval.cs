using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Domain.Security;

public enum TenantApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }
public enum OperationGrantStatus { Available, Claimed, Consumed, Revoked, Indeterminate }

public enum TenantOperationKind { Persona, ConversationPreferences, CredentialConnectionCheck, SandboxCommand, SandboxControl, SandboxRead, BrowserCommand, CredentialRead, ArtifactPublication, ArtifactVerification, WorkspaceRead, WorkspaceMutation, AttachmentRead, BackgroundStart, BackgroundReport }
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

public sealed record PreparedBrowserCredential(Guid CredentialId, Guid BindingId, string Reference, long BindingRevision,
    long CredentialRevision, long SecretRevision, string Origin, string DisplayName);
public sealed record PreparedBrowserArtifacts(bool Capture, bool Downloads, int CaptureBytes, int DownloadBytes, int CapturesPerScope, int DownloadsPerScope, string? CapturePrivacyMode = null, string? CapturePrivacyHash = null);
public sealed record PreparedBrowserCommand(Guid SessionId, string ToolName, string ArgumentsJson, string CommandHash, string TargetJson, bool ReadOnly,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBrowserCredential? Credential = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ArtifactSourcesJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FileInputOrigin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBrowserArtifacts? Artifacts = null);

public sealed record PreparedCredentialRead(Guid SessionId, Guid? CredentialId, string ArgumentsJson, string ReferencesJson);

public sealed record PreparedArtifactSource(bool Working, string Path, string ContentType, long ByteSize, string Sha256Hex, string ObservationHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ResourceRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AttachmentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? EntryId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttachmentName = null);
public sealed record PreparedArtifactPublication(Guid SessionId, string ArgumentsJson,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactSource? Source = null);
public sealed record PreparedArtifactVerification(Guid SessionId, Guid ArtifactId, string DisplayName, string ContentType, long ByteSize, string Sha256Hex);

public sealed record PreparedAttachmentRead(Guid SessionId, Guid AttachmentId, Guid EntryId, string DisplayName, string ContentType,
    long ByteSize, string Sha256Hex, string? ProcessorVersion, string ArgumentsJson, bool Vision);

public sealed record PreparedResourceReadMember(string Key, long Revision);
public sealed record PreparedAttachmentReadMember(Guid AttachmentId, Guid EntryId, string Sha256Hex);
public sealed record PreparedKnowledgeReadSource(string Identity, string Title, string Citation, string Path);
public sealed record PreparedWorkspaceRead(Guid SessionId, bool Working, string ToolName, string ArgumentsJson, string ObservationHash,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ResourceRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ResourceMembersJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedKnowledgeReadSource? Knowledge = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AttachmentMembersJson = null);
public sealed record PreparedWorkspaceMutation(Guid SessionId, bool Working, string ToolName, string ArgumentsJson,
    string ObservationHash, string Permission, string TargetsJson);

public sealed record PreparedBackgroundModel(string CatalogKey, string ProviderAlias, string ModelId, string? ReasoningEffort)
{
    public AgentRunModelPin ToPin() => new(CatalogKey, ProviderAlias, ModelId, ReasoningEffort);
}
public sealed record PreparedBackgroundStart(Guid TenantId, Guid SessionId, Guid ChildSessionId, Guid EntryId, Guid ActivationId,
    Guid RunId, Guid ResponseId, string Objective, string Title, bool ReportCompletion, string Permission, string ConfigurationHash, PreparedBackgroundModel Model);

// Exactly one typed payload is allowed. No secret or bearer authority is part of this shape.
public sealed record PreparedBackgroundReport(Guid TenantId, Guid SessionId, Guid ChildRunId, long SourceRevision,
    Guid ParentSessionId, long ParentRevision, long InputBoundarySequence, Guid ActivationId, Guid RunId, Guid ResponseId,
    string EvidenceJson, string ConfigurationHash, PreparedBackgroundModel Model);

public sealed record PreparedTenantOperation(Guid OperationId, Guid InstanceId, AgentIdentity? Persona,
    long ExpectedInstanceRevision, long ExpectedPersonaRevision, string CanonicalActionJson, string ActionHash,
    PreparedConversationPreferences? Preferences = null, PreparedCredentialConnectionCheck? CredentialCheck = null, PreparedSandboxCommand? SandboxCommand = null, PreparedSandboxControl? SandboxControl = null, PreparedSandboxRead? SandboxRead = null, PreparedBrowserCommand? BrowserCommand = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedCredentialRead? CredentialRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactPublication? ArtifactPublication = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactVerification? ArtifactVerification = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedWorkspaceRead? WorkspaceRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedWorkspaceMutation? WorkspaceMutation = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAttachmentRead? AttachmentRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBackgroundStart? BackgroundStart = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBackgroundReport? BackgroundReport = null)
{
    [JsonIgnore]
    public TenantOperationKind Kind => (Persona, Preferences, CredentialCheck, SandboxCommand, SandboxControl, SandboxRead, BrowserCommand, CredentialRead, ArtifactPublication, ArtifactVerification, WorkspaceRead, WorkspaceMutation, AttachmentRead, BackgroundStart, BackgroundReport) switch {
        (not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.Persona,
        (null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.ConversationPreferences,
        (null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.CredentialConnectionCheck,
        (null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxCommand,
        (null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxControl,
        (null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxRead,
        (null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null) => TenantOperationKind.BrowserCommand,
        (null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null) => TenantOperationKind.CredentialRead,
        (null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null) => TenantOperationKind.ArtifactPublication,
        (null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null) => TenantOperationKind.ArtifactVerification,
        (null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null) => TenantOperationKind.WorkspaceRead,
        (null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null) => TenantOperationKind.WorkspaceMutation,
        (null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null) => TenantOperationKind.AttachmentRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null) => TenantOperationKind.BackgroundStart,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null) => TenantOperationKind.BackgroundReport,
        _ => throw new ArgumentException("Exactly one supported operation payload is required.") };
    [JsonIgnore] public bool IsLocalConfiguration => Kind is TenantOperationKind.Persona or TenantOperationKind.ConversationPreferences;
    [JsonIgnore] public string Permission => BackgroundReport is not null ? "agent-runs.create" : BackgroundStart is { } background ? background.Permission : Kind == TenantOperationKind.AttachmentRead ? "attachments.read" : Kind == TenantOperationKind.WorkspaceMutation ? WorkspaceMutation!.Permission : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? "sessions.read" : WorkspaceRead.ResourceKey is null ? "workspaces.read" : "resources.read" : Kind == TenantOperationKind.ArtifactVerification ? "artifacts.read" : Kind == TenantOperationKind.ArtifactPublication ? "artifacts.create" : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId is null ? "sessions.read" : "credentials.read" : Kind == TenantOperationKind.BrowserCommand ? BrowserCommand!.Credential is not null ? "credentials.use" : BrowserCommand.ReadOnly ? "browser.read" : "browser.use" : Kind == TenantOperationKind.SandboxRead ? "sandbox.read" : Kind == TenantOperationKind.SandboxControl ? "sandbox.manage" : Kind == TenantOperationKind.SandboxCommand ? "sandbox.execute" : IsLocalConfiguration ? "agents.update" : "credentials.use";
    [JsonIgnore] public string ResourceType => BackgroundReport is not null || BackgroundStart is not null ? "tenant" : Kind == TenantOperationKind.AttachmentRead ? "attachment" : Kind == TenantOperationKind.WorkspaceMutation ? "workspace" : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? "session" : WorkspaceRead.ResourceKey is null ? "workspace" : "resource" : Kind == TenantOperationKind.ArtifactVerification ? "artifact" : Kind == TenantOperationKind.ArtifactPublication ? "session" : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId is null ? "session" : "credential" : Kind == TenantOperationKind.CredentialConnectionCheck || BrowserCommand?.Credential is not null ? "credential" : "agent";
    [JsonIgnore] public string ResourceId => BackgroundReport is { } report ? report.TenantId.ToString("D") : BackgroundStart is { } background ? background.TenantId.ToString("D") : Kind == TenantOperationKind.AttachmentRead ? AttachmentRead!.AttachmentId.ToString("D") : Kind == TenantOperationKind.WorkspaceMutation ? (WorkspaceMutation!.Working ? "working:" + WorkspaceMutation.SessionId.ToString("D") : "home:" + InstanceId.ToString("D")) : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? WorkspaceRead.SessionId.ToString("D") : WorkspaceRead.ResourceKey is { } resourceKey ? InstanceId.ToString("D") + ":" + resourceKey : (WorkspaceRead!.Working ? "working:" + WorkspaceRead.SessionId.ToString("D") : "home:" + InstanceId.ToString("D")) : (Kind == TenantOperationKind.ArtifactVerification ? ArtifactVerification!.ArtifactId : Kind == TenantOperationKind.ArtifactPublication ? ArtifactPublication!.SessionId : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId ?? CredentialRead.SessionId : Kind == TenantOperationKind.CredentialConnectionCheck ? CredentialCheck!.CredentialId
        : Kind == TenantOperationKind.BrowserCommand && BrowserCommand!.Credential is { } c ? c.CredentialId : InstanceId).ToString("D");

    // Root correlation excludes mutable witnesses; grants still bind ActionHash exactly.
    [JsonIgnore]
    public string SemanticActionJson => Kind switch
    {
        TenantOperationKind.BackgroundReport => JsonSerializer.Serialize(new { operation = "background.report", BackgroundReport!.ChildRunId, BackgroundReport.ParentSessionId }),
        TenantOperationKind.BackgroundStart => JsonSerializer.Serialize(new { operation = "background.start", BackgroundStart!.SessionId, BackgroundStart.Objective, BackgroundStart.Title, BackgroundStart.ReportCompletion }),
        TenantOperationKind.AttachmentRead => JsonSerializer.Serialize(new { operation = "attachments.read", sessionId = AttachmentRead!.SessionId, arguments = AttachmentRead.ArgumentsJson }),
        TenantOperationKind.WorkspaceMutation => JsonSerializer.Serialize(new { operation = WorkspaceMutation!.ToolName, sessionId = WorkspaceMutation.SessionId, arguments = WorkspaceMutation.ArgumentsJson }),
        TenantOperationKind.WorkspaceRead => JsonSerializer.Serialize(new { operation = WorkspaceRead!.ToolName, sessionId = WorkspaceRead.SessionId, arguments = WorkspaceRead.ArgumentsJson }),
        TenantOperationKind.ArtifactVerification => JsonSerializer.Serialize(new { operation = "artifacts.verify", verification = ArtifactVerification }),
        TenantOperationKind.ArtifactPublication => JsonSerializer.Serialize(new { operation = "artifacts.publish", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, artifact = ArtifactPublication is { Source: not null } publication ? publication with { Source = null } : ArtifactPublication }),
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
        TenantOperationKind.BrowserCommand => JsonSerializer.Serialize(new { operation = "browser.command", permission = Permission,
            resourceType = ResourceType, resourceId = ResourceId, BrowserCommand!.SessionId, BrowserCommand.ToolName, BrowserCommand.CommandHash }),
        TenantOperationKind.CredentialRead => JsonSerializer.Serialize(new { operation = "credentials.metadata.read", CredentialRead!.SessionId, CredentialRead.ArgumentsJson }),
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
