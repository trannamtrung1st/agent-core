namespace AgentCore.Infrastructure.Security;

internal static class ContinuityReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.ContinuityRead') IS NULL) OR COALESCE(({Shape}),0)";
    private static readonly string Others = string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation" }.Select(name => $"json_extract(PayloadJson, '$.Operation.{name}') IS NULL"));
    private static string Shape => """
        Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND length(Fingerprint) = 64 AND Fingerprint NOT GLOB '*[^0-9a-f]*'
        AND length(CAST(PayloadJson AS BLOB)) <= 262144
        AND json_type(PayloadJson, '$.Operation.ContinuityRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.RootSessionId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.RootSessionId')) = 36
        AND (json_type(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS 'null' OR (json_type(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')) = 36))
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.ToolName') IN ('continuity.search','continuity.get')
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson')) IS 'object'
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.SessionsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson') AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
        AND json_type(PayloadJson, '$.Operation.ContinuityRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ContinuityRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND (json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory') IS NULL OR (
            json_type(PayloadJson, '$.Operation.ContinuityRead.Memory') IS 'object'
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SessionId') IS SessionId
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.RootSessionId') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.RootSessionId')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.UserPrincipalId') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ToolName') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.ToolName')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ArgumentsJson') IS json_extract(PayloadJson, '$.Operation.ContinuityRead.ArgumentsJson')
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ResultMemoryId') IS NULL
            AND json_type(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson') IS 'text'
            AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
            AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) IS 'array'
            AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) BETWEEN 0 AND 11
            AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) <= 8192
            AND json_type(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson') IS 'text'
            AND json_valid(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson'))
            AND json_type(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson')) IS 'array'
            AND json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson')) = json_array_length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
            AND length(CAST(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.SourceLabelsJson') AS BLOB)) <= 16384
            AND length(json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ObservationHash')) = 64
            AND json_extract(PayloadJson, '$.Operation.ContinuityRead.Memory.ObservationHash') NOT GLOB '*[^0-9a-f]*'))
        AND
        """ + " " + Others;
}
