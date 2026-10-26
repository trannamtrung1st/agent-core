using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Domain.Credentials;

namespace AgentCore.Domain.Security;

// Exact local Instance plan; the existing approval/effect owner commits it once.
public sealed record PreparedManagementMutation(Guid SessionId, Guid RootSessionId, string ToolName, string ArgumentsJson,
    string ConfigurationHash, string BeforeStableHash, string AfterStableHash, string ChangesJson,
    InstanceSettingsOverrides? Settings = null, HarnessManagementState? Management = null,
    IReadOnlyList<InstanceAutomationPolicyChange>? AutomationChanges = null,
    AgentCore.Domain.Models.ModelPreference? UnattendedModel = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedManagementSkill? Skill = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedManagementResource? Resource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedHarnessCommand? Harness = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedConfigurationRemedy? Remedy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PreparedManagementBinding? Binding = null)
{
    public bool Equals(PreparedManagementMutation? other) => other is not null && JsonSerializer.Serialize(this) == JsonSerializer.Serialize(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(this));
}

// The incumbent Skill mutation's immutable domain values; no infrastructure/history DTO.
public sealed record PreparedManagementSkill(Guid TenantId, string? TargetKey, bool CreateScope, string SnapshotHash,
    DateTimeOffset PreparedAt, AgentDefinitionSkillState? DefinitionState, AgentInstanceSkill? InstanceSkill,
    string? DeleteSkillId, long? ExpectedSkillRevision, long? ExpectedStateRevision, bool Changed,
    IReadOnlyList<TenantManagementChildRead> BeforeSources, IReadOnlyList<TenantManagementChildRead> AfterSources, TenantManagementSkillResult Result);

public sealed record TenantManagementSkillReceipt(string ToolName, long BeforeInstanceRevision, string BeforeStableHash, string AfterStableHash,
    IReadOnlyList<TenantManagementChildRead> BeforeSources, IReadOnlyList<TenantManagementChildRead> AfterSources, TenantManagementSkillResult Result);

public sealed record TenantManagementSkillResult(string Key, long Revision, bool Enabled, bool Deleted,
    string? SourceKey = null, long? SourceRevision = null, bool? SourceEnabled = null);

public sealed record PreparedManagementResource(Guid TenantId, string? TargetKey, bool CreateScope, string SnapshotHash,
    DateTimeOffset PreparedAt, Guid AllocatedId, AgentDefinitionPublicationResource? CopiedSource,
    AgentDefinitionResourceState? DefinitionState, AgentInstanceResource? InstanceResource, Guid? DeleteResourceId,
    long? ExpectedResourceRevision, long? ExpectedStateRevision, bool Changed,
    IReadOnlyList<TenantManagementChildRead> BeforeSources, IReadOnlyList<TenantManagementChildRead> AfterSources,
    TenantManagementResourceResult Result, TenantManagementResourceSource? Source, TenantManagementResourceResult? Before);

// One actual root input or paired successful tool observation. This is factual source evidence, not authority.
public sealed record TenantManagementResourceSource(string Name, Guid RootRunId, string MaterialHash,
    string? ToolCallId = null, string? ToolName = null, string? ArgumentsJson = null,
    Guid? ReadRequestId = null, Guid? ReadOperationId = null,
    PreparedWorkspaceRead? Workspace = null, PreparedAttachmentRead? Attachment = null);

public sealed record TenantManagementResourceResult(string Key, long Revision, bool Enabled, bool? EnabledOverride, bool Deleted,
    string LogicalPath, AgentDefinitionResourceKind Kind, string MediaType, string ContentSha256, long ByteLength,
    string? SourceDefinitionId = null, int? SourceDefinitionVersion = null, Guid? SourceDefinitionResourceId = null);

public sealed record TenantManagementResourceReceipt(string ToolName, long BeforeInstanceRevision, string BeforeStableHash, string AfterStableHash,
    IReadOnlyList<TenantManagementChildRead> BeforeSources, IReadOnlyList<TenantManagementChildRead> AfterSources, TenantManagementResourceResult Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? HarnessChangeId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantManagementResourceResult? Before = null);

// The actual contextual Harness command and server-owned assessment identity; not another effect owner.
public sealed record PreparedHarnessCommand(string ToolName, string ArgumentsJson, HarnessInstanceChange Assessment, bool Changed = true);

// The exact inspected remedy; the ordinary settings owner still performs the change.
public sealed record PreparedConfigurationRemedy(string ArgumentsJson, TenantManagementReadReference Inspection);

// Safe binding identity and metadata witnesses only; protected Credential payloads never enter a plan.
public sealed record PreparedManagementBinding(AgentCredentialBinding Value, bool Remove, long CredentialRevision,
    string CredentialFingerprint, string DisplayName, string CredentialKind, string CredentialPurpose, bool Changed,
    IReadOnlyList<TenantManagementChildRead> BeforeSources, IReadOnlyList<TenantManagementChildRead> AfterSources);
