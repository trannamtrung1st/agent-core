namespace AgentCore.Infrastructure.Security;
internal static class ManagementMutationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson,'$.Operation.ManagementMutation') IS NULL) OR COALESCE(({Shape}),0)";
    private static string J(string field) => "json_extract(PayloadJson,'$.Operation.ManagementMutation." + field + "')";
    private static string T(string field) => "json_type(PayloadJson,'$.Operation.ManagementMutation." + field + "')";
    private static string Others => string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation", "ContinuityRead", "ExperienceOperation", "AutomationRead", "AutomationMutation", "ManagementRead" }
        .Select(n => $"json_extract(PayloadJson,'$.Operation.{n}') IS NULL"));
    private static string Shape => $"""
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission='agents.update' AND ResourceType='agent' AND ResourceId=InstanceId
        AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB))<=131072
        AND json_type(PayloadJson,'$.Operation.ManagementMutation') IS 'object'
        AND {J("SessionId")} IS SessionId AND {T("RootSessionId")} IS 'text' AND length({J("RootSessionId")})=36
        AND {J("ToolName")} IN ('settings.patch','management.policy.update','management.freeze','automation.preferences.patch')
        AND {T("ArgumentsJson")} IS 'text' AND length(CAST({J("ArgumentsJson")} AS BLOB)) BETWEEN 2 AND 24000
        AND json_valid({J("ArgumentsJson")}) AND json_type({J("ArgumentsJson")})='object'
        AND json_extract({J("ArgumentsJson")},'$.expectedInstanceRevision')=json_extract(PayloadJson,'$.Operation.ExpectedInstanceRevision')
        AND {T("ConfigurationHash")} IS 'text' AND length({J("ConfigurationHash")})=64 AND {J("ConfigurationHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("BeforeStableHash")} IS 'text' AND length({J("BeforeStableHash")})=64 AND {J("BeforeStableHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("AfterStableHash")} IS 'text' AND length({J("AfterStableHash")})=64 AND {J("AfterStableHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("ChangesJson")} IS 'text' AND length(CAST({J("ChangesJson")} AS BLOB)) BETWEEN 2 AND 48000
        AND json_valid({J("ChangesJson")}) AND json_type({J("ChangesJson")})='array'
        AND ({J("ToolName")}='settings.patch' AND {T("Settings")}='object' AND {T("AutomationChanges")}='array' AND json_array_length(PayloadJson,'$.Operation.ManagementMutation.AutomationChanges')<256
            AND {J("Management")} IS NULL AND {J("UnattendedOfferingId")} IS NULL AND {J("UnattendedReasoningEffort")} IS NULL
            OR {J("ToolName")} IN ('management.policy.update','management.freeze') AND {T("Management")}='object' AND {J("Settings")} IS NULL AND {J("AutomationChanges")} IS NULL
                AND {J("UnattendedOfferingId")} IS NULL AND {J("UnattendedReasoningEffort")} IS NULL
            OR {J("ToolName")}='automation.preferences.patch' AND {J("Settings")} IS NULL AND {J("Management")} IS NULL AND {J("AutomationChanges")} IS NULL
                AND ({J("UnattendedOfferingId")} IS NULL OR {T("UnattendedOfferingId")}='text' AND length({J("UnattendedOfferingId")})<=128)
                AND ({J("UnattendedReasoningEffort")} IS NULL OR {T("UnattendedReasoningEffort")}='text' AND length({J("UnattendedReasoningEffort")})<=64))
        AND {Others}
        """;
}
