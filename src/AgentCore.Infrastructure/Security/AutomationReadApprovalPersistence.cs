namespace AgentCore.Infrastructure.Security;
internal static class AutomationReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson,'$.Operation.AutomationRead') IS NULL) OR COALESCE(({Shape}),0)";
    private static string J(string field) => "json_extract(PayloadJson,'$.Operation.AutomationRead." + field + "')";
    private static string T(string field) => "json_type(PayloadJson,'$.Operation.AutomationRead." + field + "')";
    private static string Others => string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation", "ContinuityRead", "ExperienceOperation" }
        .Select(n => $"json_extract(PayloadJson,'$.Operation.{n}') IS NULL"));
    private static string Array(string field, int count, int bytes) => $"{T(field)} IS 'text' AND length(CAST({J(field)} AS BLOB)) BETWEEN 2 AND {bytes} AND json_valid({J(field)}) AND json_type({J(field)}) IS 'array' AND json_array_length({J(field)}) BETWEEN 0 AND {count}";
    private static string Shape => $"""
        Kind=0 AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission='automations.read' AND ResourceType='automation' AND ResourceId=json_extract({J("SourcesJson")},'$[0].AutomationId')
        AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB))<=131072
        AND json_type(PayloadJson,'$.Operation.AutomationRead') IS 'object'
        AND {J("SessionId")} IS SessionId AND {T("RootSessionId")} IS 'text' AND length({J("RootSessionId")})=36
        AND {J("ToolName")} IN ('automation.inspect','automation.list') AND {T("ArgumentsJson")} IS 'text'
        AND length(CAST({J("ArgumentsJson")} AS BLOB)) BETWEEN 2 AND 4096 AND json_valid({J("ArgumentsJson")}) AND json_type({J("ArgumentsJson")})='object'
        AND {Array("SourcesJson",8,4096)} AND json_array_length({J("SourcesJson")})>=1 AND {Array("EventSourcesJson",4,4096)}
        AND {T("ObservationJson")} IS 'text' AND length(CAST({J("ObservationJson")} AS BLOB)) BETWEEN 2 AND 10000
        AND json_valid({J("ObservationJson")}) AND json_type({J("ObservationJson")})='object'
        AND {T("ObservationHash")} IS 'text' AND length({J("ObservationHash")})=64 AND {J("ObservationHash")} NOT GLOB '*[^0-9a-f]*'
        AND ({J("ToolName")}<>'automation.inspect' OR json_extract({J("ArgumentsJson")},'$.automationId')=ResourceId)
        AND {Others}
        """;
}
