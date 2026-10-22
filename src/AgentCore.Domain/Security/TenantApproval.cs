using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;

namespace AgentCore.Domain.Security;

public enum TenantApprovalStatus { Pending, Approved, Rejected, Expired, Cancelled }
public enum OperationGrantStatus { Available, Claimed, Consumed, Revoked, Indeterminate }

public enum TenantOperationKind { Persona, ConversationPreferences, CredentialConnectionCheck, SandboxCommand, SandboxControl, SandboxRead, BrowserCommand, CredentialRead, ArtifactPublication, ArtifactVerification, WorkspaceRead, WorkspaceMutation, AttachmentRead, BackgroundStart, BackgroundReport, CompletionRead, ReportSourceRead, AccountRead, AccountAction, AutomationAdmission, MemoryOperation, ContinuityRead, ExperienceOperation, AutomationRead, AutomationMutation, ManagementRead, ManagementMutation }
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

public sealed record PreparedAccountDraft(string DraftId, string RecipientsJson, string Subject, string Body, string Sender, long Revision);
public sealed record PreparedAccountRemoteFile(string Id, string Name, string MimeType, long ByteSize, string Version, string? Md5Checksum,
    IReadOnlyList<string> Parents, bool Trashed);
public sealed record PreparedAccountAction(Guid SessionId, string ToolName, string ArgumentsJson, PreparedCredentialConnectionCheck Credential,
    AgentCore.Domain.Credentials.CredentialPurpose Purpose, string Permission, string RemoteSourcesJson,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAccountDraft? Draft = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactSource? UploadSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DestinationObservationHash = null);

public sealed record PreparedAccountOperationRead(Guid OperationId, Guid RequestId, Guid RunId, Guid SessionId, string ActionHash,
    string ToolName, string RecordedOutcome, string NativeReferencesJson);
public sealed record PreparedAccountRead(Guid SessionId, string ToolName, string ArgumentsJson, PreparedCredentialConnectionCheck Credential,
    AgentCore.Domain.Credentials.CredentialPurpose Purpose,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAccountOperationRead? Source = null);

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

public sealed record PreparedReportSourceRead(Guid ParentSessionId, Guid ChildSessionId, Guid InitialRunId,
    Guid SourceRunId, long SourceRevision, string Objective, string ObservationJson, string ObservationHash);

// Exactly one typed payload is allowed. No secret or bearer authority is part of this shape.
public sealed record PreparedBackgroundReport(Guid TenantId, Guid SessionId, Guid ChildRunId, long SourceRevision,
    Guid ParentSessionId, long ParentRevision, long InputBoundarySequence, Guid ActivationId, Guid RunId, Guid ResponseId,
    string EvidenceJson, string ConfigurationHash, PreparedBackgroundModel Model);

// Exact terminal child observation. Accounting and claim tokens remain outside read consent.
public sealed record PreparedCompletionRead(Guid ParentSessionId, Guid ChildSessionId, Guid InitialRunId,
    Guid SourceRunId, long SourceRevision, string Objective, string ObservationJson, string ObservationHash,
    string ArtifactsJson);

// A reserved occurrence is not a Session or Run. Exact work identities become real
// only in the consent settlement transaction; no execution authority is copied here.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreparedAutomationConversation(long SessionRevision, long InputBoundarySequence);

public sealed record PreparedAutomationAdmission(Guid TenantId, [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation Current, [property: JsonConverter(typeof(PreparedAutomationJsonConverter))] Automation Next, TriggerOccurrence Occurrence,
    Guid RequestId, Guid SessionId, Guid EntryId, Guid ActivationId, Guid RunId, Guid ResponseId,
    Guid? ManualInitiatorPrincipalId, string Permission, string ConfigurationHash, PreparedBackgroundModel Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedEventAdmissionSource? EventSource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationConversation? Conversation = null)
{
    [JsonIgnore]
    public IReadOnlyList<Guid> EmailSourceAccountIds
    {
        get
        {
            if (EventSource is null || Current.Triggers.SingleOrDefault(t => t.TriggerId == EventSource.TriggerId)?.Configuration
                is not CoreEventTrigger { CoreEventKey: "email.message.received" }) return [];
            return AgentCore.Domain.Email.EmailEventReferences.AccountIdsFromEvidence(Occurrence.EvidenceJson, EventSource.IsBucket);
        }
    }
    // Persistence reconstructs the closed Automation and occurrence witnesses.
    // Compare their canonical value, never their object identities.
    public bool Equals(PreparedAutomationAdmission? other) => other is not null
        && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(this));
}

public sealed record PreparedTenantOperation(Guid OperationId, Guid InstanceId, AgentIdentity? Persona,
    long ExpectedInstanceRevision, long ExpectedPersonaRevision, string CanonicalActionJson, string ActionHash,
    PreparedConversationPreferences? Preferences = null, PreparedCredentialConnectionCheck? CredentialCheck = null, PreparedSandboxCommand? SandboxCommand = null, PreparedSandboxControl? SandboxControl = null, PreparedSandboxRead? SandboxRead = null, PreparedBrowserCommand? BrowserCommand = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedCredentialRead? CredentialRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactPublication? ArtifactPublication = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedArtifactVerification? ArtifactVerification = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedWorkspaceRead? WorkspaceRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedWorkspaceMutation? WorkspaceMutation = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAttachmentRead? AttachmentRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBackgroundStart? BackgroundStart = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedBackgroundReport? BackgroundReport = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedCompletionRead? CompletionRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedReportSourceRead? ReportSourceRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAccountRead? AccountRead = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAccountAction? AccountAction = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationAdmission? AutomationAdmission = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedMemoryOperation? MemoryOperation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedContinuityRead? ContinuityRead = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedExperienceOperation? ExperienceOperation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationRead? AutomationRead = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedAutomationMutation? AutomationMutation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedManagementRead? ManagementRead = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedManagementMutation? ManagementMutation = null)
{
    [JsonIgnore]
    public TenantOperationKind Kind => (Persona, Preferences, CredentialCheck, SandboxCommand, SandboxControl, SandboxRead, BrowserCommand, CredentialRead, ArtifactPublication, ArtifactVerification, WorkspaceRead, WorkspaceMutation, AttachmentRead, BackgroundStart, BackgroundReport, CompletionRead, ReportSourceRead, AccountRead, AccountAction, AutomationAdmission, MemoryOperation, ContinuityRead, ExperienceOperation, AutomationRead, AutomationMutation, ManagementRead, ManagementMutation) switch {
        (not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.Persona,
        (null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.ConversationPreferences,
        (null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.CredentialConnectionCheck,
        (null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxCommand,
        (null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxControl,
        (null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.SandboxRead,
        (null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.BrowserCommand,
        (null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.CredentialRead,
        (null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.ArtifactPublication,
        (null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.ArtifactVerification,
        (null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.WorkspaceRead,
        (null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.WorkspaceMutation,
        (null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.AttachmentRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.BackgroundStart,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.BackgroundReport,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.CompletionRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.ReportSourceRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null, null) => TenantOperationKind.AccountRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null, null) => TenantOperationKind.AccountAction,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null, null) => TenantOperationKind.AutomationAdmission,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null, null) => TenantOperationKind.MemoryOperation,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null, null) => TenantOperationKind.ContinuityRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null, null) => TenantOperationKind.ExperienceOperation,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null, null) => TenantOperationKind.AutomationRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null, null) => TenantOperationKind.AutomationMutation,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null, null) => TenantOperationKind.ManagementRead,
        (null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, not null) => TenantOperationKind.ManagementMutation,
        _ => throw new ArgumentException("Exactly one supported operation payload is required.") };
    [JsonIgnore] public bool IsLocalConfiguration => Kind is TenantOperationKind.Persona or TenantOperationKind.ConversationPreferences;
    [JsonIgnore] public string Permission => ManagementMutation is not null ? "agents.update" : ManagementRead is not null ? "agents.read" : AutomationMutation is { } mutation ? mutation.ToolName == "automation.create" ? "automations.create" : mutation.ToolName == "automation.delete" ? "automations.delete" : mutation.ToolName == "automation.run" ? "automations.run" : "automations.update" : AutomationRead is not null ? "automations.read" : ExperienceOperation is { } experience ? experience.ToolName == "experience.source" ? experience.Source?.Kind == AgentCore.Domain.Experience.ExperienceSourceKind.AgentRun ? "agent-runs.read" : "sessions.read" : "memory.manage" : ContinuityRead is not null ? "sessions.read" : MemoryOperation is { } memory ? memory.ToolName is "continuity.search" or "continuity.get" ? "memory.read" : "memory.manage" : AutomationAdmission is { } admission ? admission.Permission : AccountAction is { } accountAction ? accountAction.Permission : AccountRead is not null ? "credentials.use" : ReportSourceRead is not null || CompletionRead is not null ? "agent-runs.read" : BackgroundReport is not null ? "agent-runs.create" : BackgroundStart is { } background ? background.Permission : Kind == TenantOperationKind.AttachmentRead ? "attachments.read" : Kind == TenantOperationKind.WorkspaceMutation ? WorkspaceMutation!.Permission : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? "sessions.read" : WorkspaceRead.ResourceKey is null ? "workspaces.read" : "resources.read" : Kind == TenantOperationKind.ArtifactVerification ? "artifacts.read" : Kind == TenantOperationKind.ArtifactPublication ? "artifacts.create" : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId is null ? "sessions.read" : "credentials.read" : Kind == TenantOperationKind.BrowserCommand ? BrowserCommand!.Credential is not null ? "credentials.use" : BrowserCommand.ReadOnly ? "browser.read" : "browser.use" : Kind == TenantOperationKind.SandboxRead ? "sandbox.read" : Kind == TenantOperationKind.SandboxControl ? "sandbox.manage" : Kind == TenantOperationKind.SandboxCommand ? "sandbox.execute" : IsLocalConfiguration ? "agents.update" : "credentials.use";
    [JsonIgnore] public string ResourceType => ManagementMutation is not null ? "agent" : ManagementRead is not null ? "agent" : AutomationMutation is { } mutation ? mutation.Current is null ? "tenant" : "automation" : AutomationRead is not null ? "automation" : ExperienceOperation is { } experience ? experience.ToolName == "experience.source" ? experience.Source?.Kind == AgentCore.Domain.Experience.ExperienceSourceKind.AgentRun ? "agent-run" : "session" : "agent" : ContinuityRead is not null ? "session" : MemoryOperation is not null ? "agent" : AutomationAdmission is { } admission ? admission.Permission switch { "automations.run" => "automation", "sessions.read" => "session", "agents.chat" => "agent", "webhooks.read" => "webhook", "core-events.read" => "event", "credentials.read" or "credentials.use" or "email.read" => "credential", _ => "tenant" } : AccountAction is { } action ? action.Permission == "workspaces.write" ? "workspace" : "credential" : AccountRead is not null ? "credential" : ReportSourceRead is not null || CompletionRead is not null ? "agent-run" : BackgroundReport is not null || BackgroundStart is not null ? "tenant" : Kind == TenantOperationKind.AttachmentRead ? "attachment" : Kind == TenantOperationKind.WorkspaceMutation ? "workspace" : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? "session" : WorkspaceRead.ResourceKey is null ? "workspace" : "resource" : Kind == TenantOperationKind.ArtifactVerification ? "artifact" : Kind == TenantOperationKind.ArtifactPublication ? "session" : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId is null ? "session" : "credential" : Kind == TenantOperationKind.CredentialConnectionCheck || BrowserCommand?.Credential is not null ? "credential" : "agent";
    [JsonIgnore] public string ResourceId => ManagementMutation is not null ? InstanceId.ToString("D") : ManagementRead is not null ? InstanceId.ToString("D") : AutomationMutation is { } mutation ? (mutation.Current is null ? mutation.Proposed.Owner.TenantId!.Value : mutation.Proposed.AutomationId).ToString("D") : AutomationRead is { } automationRead ? (JsonSerializer.Deserialize<TenantAutomationReadSource[]>(automationRead.SourcesJson)?.FirstOrDefault()?.AutomationId ?? throw new ArgumentException("An exact read consent requires one source.")).ToString("D") : ExperienceOperation is { } experience ? (experience.ToolName == "experience.source" ? experience.Source!.SourceId : InstanceId).ToString("D") : ContinuityRead is { } continuity ? continuity.SessionId.ToString("D") : MemoryOperation is not null ? InstanceId.ToString("D") : AutomationAdmission is { } admission ? (admission.Permission switch { "automations.run" => admission.Current.AutomationId, "sessions.read" => admission.SessionId, "agents.chat" => admission.Current.Owner.AgentInstanceId, "webhooks.read" => admission.EventSource!.ResourceId!.Value, "core-events.read" => admission.EventSource!.EventIds[0], "credentials.read" or "credentials.use" or "email.read" => admission.EmailSourceAccountIds[0], _ => admission.TenantId }).ToString("D") : AccountAction is { } action ? action.Permission == "workspaces.write"
        ? (AccountActionWorking(action) ? "working:" + action.SessionId.ToString("D") : "home:" + InstanceId.ToString("D"))
        : action.Credential.CredentialId.ToString("D") : AccountRead is { } accountRead ? accountRead.Credential.CredentialId.ToString("D") : ReportSourceRead is { } sourceRead ? sourceRead.SourceRunId.ToString("D") : CompletionRead is { } completion ? completion.SourceRunId.ToString("D") : BackgroundReport is { } report ? report.TenantId.ToString("D") : BackgroundStart is { } background ? background.TenantId.ToString("D") : Kind == TenantOperationKind.AttachmentRead ? AttachmentRead!.AttachmentId.ToString("D") : Kind == TenantOperationKind.WorkspaceMutation ? (WorkspaceMutation!.Working ? "working:" + WorkspaceMutation.SessionId.ToString("D") : "home:" + InstanceId.ToString("D")) : Kind == TenantOperationKind.WorkspaceRead ? (WorkspaceRead!.ResourceMembersJson is not null || WorkspaceRead.AttachmentMembersJson is not null) ? WorkspaceRead.SessionId.ToString("D") : WorkspaceRead.ResourceKey is { } resourceKey ? InstanceId.ToString("D") + ":" + resourceKey : (WorkspaceRead!.Working ? "working:" + WorkspaceRead.SessionId.ToString("D") : "home:" + InstanceId.ToString("D")) : (Kind == TenantOperationKind.ArtifactVerification ? ArtifactVerification!.ArtifactId : Kind == TenantOperationKind.ArtifactPublication ? ArtifactPublication!.SessionId : Kind == TenantOperationKind.CredentialRead ? CredentialRead!.CredentialId ?? CredentialRead.SessionId : Kind == TenantOperationKind.CredentialConnectionCheck ? CredentialCheck!.CredentialId
        : Kind == TenantOperationKind.BrowserCommand && BrowserCommand!.Credential is { } c ? c.CredentialId : InstanceId).ToString("D");

    private static bool AccountActionWorking(PreparedAccountAction action)
    {
        using var json = JsonDocument.Parse(action.ArgumentsJson);
        return json.RootElement.GetProperty("path").GetString()!.StartsWith("/working/", StringComparison.Ordinal);
    }

    // Root correlation excludes mutable witnesses; grants still bind ActionHash exactly.
    [JsonIgnore]
    public string SemanticActionJson => Kind switch
    {
        TenantOperationKind.ExperienceOperation => JsonSerializer.Serialize(new { operation = ExperienceOperation!.ToolName, sessionId = ExperienceOperation.SessionId, arguments = ExperienceOperation.ArgumentsJson }),
        TenantOperationKind.ContinuityRead => JsonSerializer.Serialize(new { operation = ContinuityRead!.ToolName, ContinuityRead.SessionId, ContinuityRead.ArgumentsJson }),
        TenantOperationKind.MemoryOperation => JsonSerializer.Serialize(new { operation = MemoryOperation!.ToolName, MemoryOperation.SessionId, MemoryOperation.ArgumentsJson }),
        TenantOperationKind.AutomationAdmission => JsonSerializer.Serialize(new { operation = "automations.occurrence.admit", AutomationAdmission!.Current.AutomationId, AutomationAdmission.Occurrence.OccurrenceId, AutomationAdmission.Permission }),
        TenantOperationKind.AccountAction => JsonSerializer.Serialize(new { operation = "accounts.action", AccountAction!.SessionId, AccountAction.ToolName, AccountAction.ArgumentsJson, AccountAction.Credential.CredentialId }),
        TenantOperationKind.AccountRead => JsonSerializer.Serialize(new { operation = "accounts.read", AccountRead!.SessionId, AccountRead.ToolName, AccountRead.ArgumentsJson, AccountRead.Credential.CredentialId }),
        TenantOperationKind.ReportSourceRead => JsonSerializer.Serialize(new { operation = "background.report.source.read", ReportSourceRead!.ParentSessionId, ReportSourceRead.ChildSessionId, ReportSourceRead.InitialRunId }),
        TenantOperationKind.CompletionRead => JsonSerializer.Serialize(new { operation = "background.inspect", CompletionRead!.ParentSessionId, CompletionRead.ChildSessionId, CompletionRead.InitialRunId }),
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
        TenantOperationKind.AutomationMutation => JsonSerializer.Serialize(new { operation = AutomationMutation!.ToolName, AutomationMutation.SessionId, AutomationMutation.ArgumentsJson, AutomationMutation.IntentHash }),
        TenantOperationKind.ManagementMutation => JsonSerializer.Serialize(new { operation = ManagementMutation!.ToolName, ManagementMutation.SessionId,
            arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ManagementMutation.ArgumentsJson)!.Where(p => p.Key != "expectedInstanceRevision").ToDictionary(p => p.Key, p => p.Value) }),
        TenantOperationKind.ManagementRead => JsonSerializer.Serialize(new { operation = ManagementRead!.ToolName, ManagementRead.SessionId, ArgumentsJson = TenantManagementReadSelection.SemanticArguments(ManagementRead.ToolName, ManagementRead.ArgumentsJson) }),
        TenantOperationKind.AutomationRead => JsonSerializer.Serialize(new { operation = AutomationRead!.ToolName, AutomationRead.SessionId, AutomationRead.ArgumentsJson }),
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
