namespace AgentCore.Infrastructure.Security;
internal static class AutomationMutationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson,'$.Operation.AutomationMutation') IS NULL) OR COALESCE(({Shape}),0)";
    private static string J(string field) => "json_extract(PayloadJson,'$.Operation.AutomationMutation." + field + "')";
    private static string T(string field) => "json_type(PayloadJson,'$.Operation.AutomationMutation." + field + "')";
    private static string Others => string.Join(" AND ", new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport", "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction", "AutomationAdmission", "MemoryOperation", "ContinuityRead", "ExperienceOperation", "AutomationRead" }
        .Select(n => $"json_extract(PayloadJson,'$.Operation.{n}') IS NULL"));
    private static string Shape => $"""
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission IN ('automations.create','automations.update','automations.delete','automations.run')
        AND length(Fingerprint)=64 AND Fingerprint NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB))<=1048576
        AND json_type(PayloadJson,'$.Operation.AutomationMutation') IS 'object'
        AND {J("SessionId")} IS SessionId AND {T("RootSessionId")} IS 'text' AND length({J("RootSessionId")})=36
        AND {T("SemanticReviewRequired")} IN ('true','false')
        AND {J("ToolName")} IN ('automation.create','automation.update','automation.enable','automation.disable','automation.delete','automation.run')
        AND Permission IS CASE {J("ToolName")} WHEN 'automation.create' THEN 'automations.create' WHEN 'automation.delete' THEN 'automations.delete' WHEN 'automation.run' THEN 'automations.run' ELSE 'automations.update' END
        AND {T("ArgumentsJson")} IS 'text' AND length(CAST({J("ArgumentsJson")} AS BLOB)) BETWEEN 2 AND 16384
        AND json_valid({J("ArgumentsJson")}) AND json_type({J("ArgumentsJson")})='object'
        AND {T("IntentHash")} IS 'text' AND length({J("IntentHash")})=64 AND {J("IntentHash")} NOT GLOB '*[^0-9a-f]*'
        AND {T("Proposed")} IS 'object' AND {J("Proposed.Owner.TenantId")} IS lower(TenantId)
        AND {J("Proposed.Owner.AgentInstanceId")} IS InstanceId AND {J("Proposed.Owner.ExecutorPrincipalId")} IS lower(ExecutorPrincipalId)
        AND {J("Proposed.Owner.ProfileIdOrNull")} IS NULL AND {T("Proposed.AutomationId")} IS 'text' AND length({J("Proposed.AutomationId")})=36
        AND (({J("ToolName")}='automation.create' AND {J("Current")} IS NULL AND {J("Proposed.Revision")}=1 AND ResourceType='tenant' AND ResourceId=lower(TenantId))
          OR ({J("ToolName")}<>'automation.create' AND {T("Current")} IS 'object' AND ResourceType='automation' AND ResourceId={J("Proposed.AutomationId")}
            AND {J("Current.AutomationId")} IS ResourceId AND {J("Current.Owner.TenantId")} IS lower(TenantId)
            AND {J("Current.Owner.AgentInstanceId")} IS InstanceId AND {J("Current.Owner.ExecutorPrincipalId")} IS lower(ExecutorPrincipalId)
            AND {J("Current.Revision")}>=1 AND {J("Proposed.Revision")}={J("Current.Revision")}+1
            AND json_extract({J("ArgumentsJson")},'$.automationId') IS ResourceId AND json_extract({J("ArgumentsJson")},'$.expectedRevision') IS {J("Current.Revision")}))
        AND (({J("ToolName")}='automation.run' AND {T("Invocation")} IS 'object' AND OriginalInitiatorPrincipalId IS NOT NULL
            AND {J("Current.Status")}=0 AND {J("Proposed.Status")}=0
            AND {T("Invocation.SessionId")} IS 'text' AND length({J("Invocation.SessionId")})=36 AND {J("Invocation.SessionId")}<>SessionId
            AND {T("Invocation.EntryId")} IS 'text' AND length({J("Invocation.EntryId")})=36
            AND {T("Invocation.ActivationId")} IS 'text' AND length({J("Invocation.ActivationId")})=36
            AND {T("Invocation.RunId")} IS 'text' AND length({J("Invocation.RunId")})=36 AND {J("Invocation.RunId")}<>RunId
            AND {T("Invocation.ResponseId")} IS 'text' AND length({J("Invocation.ResponseId")})=36
            AND {T("Invocation.ConfigurationHash")} IS 'text' AND length({J("Invocation.ConfigurationHash")})=64 AND {J("Invocation.ConfigurationHash")} NOT GLOB '*[^0-9a-f]*'
            AND {T("Invocation.Model")} IS 'object' AND {T("Invocation.Model.OfferingId")} IS 'text'
            AND {T("Invocation.Model.ConnectionId")} IS 'text' AND {T("Invocation.Model.ExternalModelId")} IS 'text'
            AND (({J("Proposed.TargetSessionId")} IS NULL AND {J("Invocation.Conversation")} IS NULL)
                OR ({J("Proposed.TargetSessionId")} IS {J("Invocation.SessionId")} AND {T("Invocation.Conversation")} IS 'object'
                    AND {J("Invocation.Conversation.SessionRevision")}>0 AND {J("Invocation.Conversation.InputBoundarySequence")}>=0)))
            OR ({J("ToolName")}<>'automation.run' AND {J("Invocation")} IS NULL))
        AND {Others}
        """;
}
