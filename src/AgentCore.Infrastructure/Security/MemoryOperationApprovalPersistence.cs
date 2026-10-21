namespace AgentCore.Infrastructure.Security;

internal static class MemoryOperationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.MemoryOperation') IS NULL) OR COALESCE(({Shape}), 0)";
    private static readonly string OtherPayloads = string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead",
        "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission" }
        .Select(name => $"json_extract(PayloadJson, '$.Operation.{name}') IS NULL"));
    private static string Shape => """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt
        AND length(Fingerprint) = 64 AND Fingerprint NOT GLOB '*[^0-9a-f]*'
        AND ResourceType = 'agent' AND ResourceId = InstanceId AND AutomationId IS NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.MemoryOperation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.RootSessionId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.RootSessionId')) = 36
        AND (json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'null' OR (
            json_type(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')) = 36))
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get','memory.forget','memory.consolidate')
        AND Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get') THEN 'memory.read' ELSE 'memory.manage' END
        AND (Permission = 'memory.manage' OR Kind = 0)
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.ArgumentsJson')) IS 'object'
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourcesJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
        AND json_type(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.MemoryOperation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND CASE WHEN json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' THEN
            json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId')) = 36
            AND json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) BETWEEN 2 AND 8
            ELSE json_type(PayloadJson, '$.Operation.MemoryOperation.ResultMemoryId') IS 'null' END
        AND (json_extract(PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('continuity.get','memory.forget')
            OR json_array_length(json_extract(PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) = 1)
        AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND
        """ + " " + OtherPayloads;
}
