namespace AgentCore.Infrastructure.Security;

internal static class BackgroundReportApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL) OR COALESCE(({Shape}),0)";
    private static string Shape => """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'agent-runs.create' AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundReport') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ChildRunId') IS RunId
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.SourceRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.InputBoundarySequence') >= 0
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson') IS 'text'
        AND json_valid(json_extract(PayloadJson, '$.Operation.BackgroundReport.EvidenceJson'))
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model') IS 'object'
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        """ + Ids + string.Concat(new[] { "OfferingId", "ConnectionId", "ExternalModelId" }.Select(p =>
            $" AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.{p}') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.{p}')) BETWEEN 1 AND {(p == "ExternalModelId" ? 255 : 128)}"))
        + string.Concat(new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead", "BackgroundStart" }
            .Select(p => $" AND json_extract(PayloadJson, '$.Operation.{p}') IS NULL"));
    private static string Ids
    {
        get
        {
            var fields = new[] { "ParentSessionId", "ActivationId", "RunId", "ResponseId" };
            string Value(string p) => $"json_extract(PayloadJson, '$.Operation.BackgroundReport.{p}')";
            return string.Concat(fields.Select(f => $" AND json_type(PayloadJson, '$.Operation.BackgroundReport.{f}') IS 'text' AND length({Value(f)}) = 36 AND {Value(f)} NOT GLOB '*[^0-9a-f-]*' AND substr({Value(f)},9,1) IS '-' AND substr({Value(f)},14,1) IS '-' AND substr({Value(f)},19,1) IS '-' AND substr({Value(f)},24,1) IS '-' AND {Value(f)} IS NOT SessionId AND {Value(f)} IS NOT RunId AND {Value(f)} IS NOT lower(TenantId)"))
                + string.Concat(fields.SelectMany((f, i) => fields.Skip(i + 1).Select(other => $" AND {Value(f)} IS NOT {Value(other)}")));
        }
    }
}
