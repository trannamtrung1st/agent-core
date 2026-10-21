namespace AgentCore.Infrastructure.Security;

internal static class ExperienceOperationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson,'$.Operation.ExperienceOperation') IS NULL AND (json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience') IS NULL OR COALESCE(({ReadShape}),0))) OR COALESCE(({OperationShape}),0)";
    private static string J(string field) => "json_extract(PayloadJson,'$.Operation.ExperienceOperation." + field + "')";
    private static string T(string field) => "json_type(PayloadJson,'$.Operation.ExperienceOperation." + field + "')";
    private static string JsonArray(string field, int count, int bytes) => $"{T(field)} IS 'text' AND length(CAST({J(field)} AS BLOB)) BETWEEN 2 AND {bytes} AND json_valid({J(field)}) AND json_type({J(field)}) IS 'array' AND json_array_length({J(field)}) BETWEEN 0 AND {count}";
    private static readonly string Others = string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation", "ContinuityRead" }.Select(n => $"json_extract(PayloadJson,'$.Operation.{n}') IS NULL"));
    private static string OperationShape => $"""
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB))<=262144
        AND json_type(PayloadJson,'$.Operation.ExperienceOperation') IS 'object'
        AND {J("SessionId")} IS SessionId AND {T("RootSessionId")} IS 'text' AND length({J("RootSessionId")})=36
        AND ({J("UserPrincipalId")} IS NULL OR ({T("UserPrincipalId")} IS 'text' AND length({J("UserPrincipalId")})=36))
        AND {T("ExperienceRevision")} IS 'integer' AND {J("ExperienceRevision")}>0 AND {T("MaintenanceRevision")} IS 'integer' AND {J("MaintenanceRevision")}>=0
        AND {T("ArgumentsJson")} IS 'text' AND length(CAST({J("ArgumentsJson")} AS BLOB)) BETWEEN 2 AND 24576 AND json_valid({J("ArgumentsJson")}) AND json_type({J("ArgumentsJson")}) IS 'object'
        AND {JsonArray("SourcesJson", 8, 8192)} AND {JsonArray("SourceLabelsJson", 8, 8192)}
        AND {T("ObservationHash")} IS 'text' AND length({J("ObservationHash")})=64 AND {J("ObservationHash")} NOT GLOB '*[^0-9a-f]*'
        AND (({J("ToolName")}='experience.source' AND Kind=0 AND {J("ResultExperienceId")} IS NULL
            AND (({J("Source.Kind")}=0 AND Permission='sessions.read' AND ResourceType='session') OR ({J("Source.Kind")}=1 AND Permission='agent-runs.read' AND ResourceType='agent-run'))
            AND ResourceId={J("Source.SourceId")})
            OR ({J("ToolName")} IN ('experience.record','experience.consolidate') AND Permission='memory.manage' AND ResourceType='agent' AND ResourceId=InstanceId
                AND {T("ResultExperienceId")} IS 'text' AND length({J("ResultExperienceId")})=36))
        AND (({J("ToolName")} IN ('experience.source','experience.record') AND {T("Source")} IS 'object' AND {J("Source.Kind")} IN (0,1)
            AND {T("Source.SourceId")} IS 'text' AND length({J("Source.SourceId")})=36 AND {T("Source.SessionId")} IS 'text' AND length({J("Source.SessionId")})=36
            AND {T("Source.ThroughCursor")} IS 'integer' AND {J("Source.ThroughCursor")}>0
            AND {T("Source.ObservationHash")} IS 'text' AND length({J("Source.ObservationHash")})=64 AND {J("Source.ObservationHash")} NOT GLOB '*[^0-9a-f]*'
            AND json_array_length({J("SourcesJson")})=0 AND json_array_length({J("SourceLabelsJson")})=1 AND {J("MaintenanceRevision")}=0)
            OR ({J("ToolName")}='experience.consolidate' AND {J("Source")} IS NULL AND {J("MaintenanceRevision")}>0
                AND json_array_length({J("SourcesJson")}) BETWEEN 2 AND 8 AND json_array_length({J("SourceLabelsJson")})=json_array_length({J("SourcesJson")})))
        AND {Others}
        """;
    private static string ReadShape => """
        json_type(PayloadJson,'$.Operation.ContinuityRead.Experience') IS 'object'
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SessionId') IS SessionId
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.RootSessionId') IS json_extract(PayloadJson,'$.Operation.ContinuityRead.RootSessionId')
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.UserPrincipalId') IS json_extract(PayloadJson,'$.Operation.ContinuityRead.UserPrincipalId')
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.ToolName') IS json_extract(PayloadJson,'$.Operation.ContinuityRead.ToolName')
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.ArgumentsJson') IS json_extract(PayloadJson,'$.Operation.ContinuityRead.ArgumentsJson')
        AND json_type(PayloadJson,'$.Operation.ContinuityRead.Experience.ExperienceRevision') IS 'integer' AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.ExperienceRevision')>0
        AND json_type(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson'))
        AND json_type(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson')) BETWEEN 0 AND 11
        AND json_type(PayloadJson,'$.Operation.ContinuityRead.Experience.SourceLabelsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourceLabelsJson') AS BLOB)) BETWEEN 2 AND 8192
        AND json_valid(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourceLabelsJson'))
        AND json_type(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourceLabelsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourceLabelsJson'))=json_array_length(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson'))
        AND json_type(PayloadJson,'$.Operation.ContinuityRead.Experience.ObservationHash') IS 'text' AND length(json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.ObservationHash'))=64
        AND json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        """;
}
