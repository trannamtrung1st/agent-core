namespace AgentCore.Infrastructure.Security;

internal static class AutomationAdmissionApprovalPersistence
{
    internal static string Extend(string prior) => $"(RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND ({prior})) OR COALESCE(({Shape}),0)";
    internal static string Shape => """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND ExpiresAt - CreatedAt <= 86400000 AND length(Fingerprint) = 64
        AND RunId IS NULL AND SessionId IS NULL AND AutomationId IS NOT NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission IN ('automations.run','sessions.create','agent-runs.create')
        AND ResourceType IS CASE WHEN Permission = 'automations.run' THEN 'automation' ELSE 'tenant' END
        AND ResourceId IS CASE WHEN Permission = 'automations.run' THEN AutomationId ELSE lower(TenantId) END
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RequestId') IS lower(RequestId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Status') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Status') IN (0,1)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Revision') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Revision') + 1
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.AgentInstanceId') IS InstanceId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.ExecutorPrincipalId') IS lower(ExecutorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner.profileId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Owner') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.Owner')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AutomationId') IS AutomationId
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,2)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Disposition') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.RoutingRevision') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionSessionId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AcceptedAgentRunId') IS NULL
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') = 0 AND OriginalInitiatorPrincipalId IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') = 2 AND OriginalInitiatorPrincipalId IS NOT NULL))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ManualInitiatorPrincipalId') IS lower(OriginalInitiatorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS json_extract(PayloadJson, '$.RunId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS json_extract(PayloadJson, '$.SessionId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS RootActivationId
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= 262144
        """ + Ids + string.Concat(new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand",
            "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport",
            "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction" }.Select(p => $" AND json_extract(PayloadJson, '$.Operation.{p}') IS NULL"));
    private static string Ids
    {
        get
        {
            var fields = new[] { "RequestId", "SessionId", "EntryId", "ActivationId", "RunId", "ResponseId" };
            string Value(string p) => $"json_extract(PayloadJson, '$.Operation.AutomationAdmission.{p}')";
            return string.Concat(fields.Select(p => $" AND json_type(PayloadJson, '$.Operation.AutomationAdmission.{p}') IS 'text' AND length({Value(p)}) = 36 AND {Value(p)} NOT GLOB '*[^0-9a-f-]*' AND {Value(p)} IS NOT '00000000-0000-0000-0000-000000000000' AND substr({Value(p)},9,1) IS '-' AND substr({Value(p)},14,1) IS '-' AND substr({Value(p)},19,1) IS '-' AND substr({Value(p)},24,1) IS '-'"))
                + string.Concat(fields.SelectMany((p, i) => fields.Skip(i + 1).Select(other => $" AND {Value(p)} IS NOT {Value(other)}")));
        }
    }
}
