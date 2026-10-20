namespace AgentCore.Infrastructure.Security;

internal static class AutomationAdmissionApprovalPersistence
{
    internal static string Extend(string prior) => $"(RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND json_extract(PayloadJson, '$.Operation.AutomationAdmission') IS NULL AND ({prior})) OR COALESCE(({Shape}),0)";
    internal static string Shape => """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND ExpiresAt - CreatedAt <= 86400000 AND length(Fingerprint) = 64
        AND RunId IS NULL AND SessionId IS NULL AND AutomationId IS NOT NULL
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND Permission IN ('automations.run','sessions.create','agent-runs.create','webhooks.read','core-events.read')
        AND (Permission NOT IN ('webhooks.read','core-events.read') OR Kind = 0)
        AND ResourceType IS CASE Permission WHEN 'automations.run' THEN 'automation' WHEN 'webhooks.read' THEN 'webhook' WHEN 'core-events.read' THEN 'event' ELSE 'tenant' END
        AND ResourceId IS CASE Permission WHEN 'automations.run' THEN AutomationId WHEN 'webhooks.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') WHEN 'core-events.read' THEN json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') ELSE lower(TenantId) END
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
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,2,3)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.Disposition') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.RoutingRevision') = 0
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.ExecutionSessionId') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.AcceptedAgentRunId') IS NULL
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,1,3) AND OriginalInitiatorPrincipalId IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') = 2 AND OriginalInitiatorPrincipalId IS NOT NULL))
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ManualInitiatorPrincipalId') IS lower(OriginalInitiatorPrincipalId)
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.RunId') IS json_extract(PayloadJson, '$.RunId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.SessionId') IS json_extract(PayloadJson, '$.SessionId')
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ActivationId') IS RootActivationId
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.AutomationAdmission.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= CASE WHEN json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object' THEN 1048576 ELSE 262144 END
        """ + " " + EventSource + Ids + string.Concat(new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand",
            "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart", "BackgroundReport",
            "CompletionRead", "ReportSourceRead", "AccountRead", "AccountAction" }.Select(p => $" AND json_extract(PayloadJson, '$.Operation.{p}') IS NULL"));
    private static string EventSource => """
        AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (0,2)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS NULL
          AND Permission NOT IN ('webhooks.read','core-events.read'))
        OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind') IN (1,3)
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource') IS 'object'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TenantId') IS lower(TenantId)
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.TriggerId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.TriggerId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceEventId')
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.DedupeKey') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') IN ('true','false')
          AND json_type(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') IS 'array'
          AND json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') BETWEEN 1 AND 24
          AND (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.IsBucket') = 1
            OR (json_array_length(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') = 1
                AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds[0]') IS json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceId')))
          AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash')) = 64
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceHash') NOT GLOB '*[^0-9a-f]*'
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Current.SchemaVersion') = 2
          AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.Next.SchemaVersion') = 2
          AND ((json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 1
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') >= 1
            AND length(json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId')) = 36
            AND Permission <> 'core-events.read')
           OR (json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.SourceKind') = 3
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceRevision') IS NULL
            AND json_extract(PayloadJson, '$.Operation.AutomationAdmission.EventSource.ResourceId') IS NULL
            AND Permission <> 'webhooks.read'))))
        """;
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
