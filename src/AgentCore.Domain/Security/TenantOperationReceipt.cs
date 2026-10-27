using System.Text.Json.Serialization;

namespace AgentCore.Domain.Security;

public enum TenantOperationOutcome { Completed, Denied, Indeterminate }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TenantTaskAdmissionReceipt(Guid TaskId, long Revision, Guid? WorkerSessionId, Guid? InitialWorkerRunId)
{
    public void Validate()
    {
        if (TaskId == Guid.Empty || Revision != 1 || WorkerSessionId == Guid.Empty || InitialWorkerRunId == Guid.Empty
            || (WorkerSessionId is null) != (InitialWorkerRunId is null)) throw new ArgumentException("Invalid exact Task admission receipt.");
    }
}

// An immutable effect result, not an authority token. No content or credential is copied into it.
public sealed record TenantOperationReceipt(Guid OperationId, Guid? RequestId, Guid TenantId, Guid RunId, Guid SessionId, Guid RootActivationId,
    Guid ExecutorPrincipalId, Guid InstanceId, string ActionHash, TenantOperationOutcome Outcome, string ReasonCode,
    long InstanceRevision, long PersonaRevision, DateTimeOffset RecordedAt, string? SourceCallId = null, string? ParametersHash = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BrowserArtifactsJson = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantAutomationMutationReceipt? AutomationMutation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantManagementSkillReceipt? ManagementSkill = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantManagementResourceReceipt? ManagementResource = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantPublicDownloadReceipt? PublicDownload = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantHttpReceipt? Http = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TenantTaskAdmissionReceipt? TaskAdmission = null);
