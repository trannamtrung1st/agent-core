namespace AgentCore.Infrastructure.Security;
internal static class ManagementReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson,'$.Operation.ManagementRead') IS NULL) OR COALESCE(({Shape}),0)";
    private static string J(string field) => "json_extract(PayloadJson,'$.Operation.ManagementRead." + field + "')";
    private static string T(string field) => "json_type(PayloadJson,'$.Operation.ManagementRead." + field + "')";
    private static string Others => string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation", "ContinuityRead", "ExperienceOperation", "AutomationRead", "AutomationMutation" }
        .Select(n => $"json_extract(PayloadJson,'$.Operation.{n}') IS NULL"));
    private static string Shape => $"""
        Kind=0 AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission='agents.read' AND ResourceType='agent' AND ResourceId=InstanceId
        AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB))<=131072
        AND json_type(PayloadJson,'$.Operation.ManagementRead') IS 'object'
        AND {J("SessionId")} IS SessionId AND {T("RootSessionId")} IS 'text' AND length({J("RootSessionId")})=36
        AND {J("ToolName")} IN ('management.inspect','settings.list','settings.inspect','automation.preferences.inspect')
        AND {T("ArgumentsJson")} IS 'text' AND length(CAST({J("ArgumentsJson")} AS BLOB)) BETWEEN 2 AND 4096
        AND json_valid({J("ArgumentsJson")}) AND json_type({J("ArgumentsJson")})='object'
        AND {T("InstanceRevision")} IS 'integer' AND {J("InstanceRevision")}>0 AND {J("InstanceRevision")}=json_extract(PayloadJson,'$.Operation.ExpectedInstanceRevision')
        AND {T("ConfigurationHash")} IS 'text' AND length({J("ConfigurationHash")})=64 AND {J("ConfigurationHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("StableConfigurationHash")} IS 'text' AND length({J("StableConfigurationHash")})=64 AND {J("StableConfigurationHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("ObservationJson")} IS 'text' AND length(CAST({J("ObservationJson")} AS BLOB)) BETWEEN 2 AND 16000
        AND json_valid({J("ObservationJson")}) AND json_type({J("ObservationJson")})='object'
        AND {T("ObservationHash")} IS 'text' AND length({J("ObservationHash")})=64 AND {J("ObservationHash")} NOT GLOB '*[^0-9a-f]*'
        AND {Others}
        """;
}
