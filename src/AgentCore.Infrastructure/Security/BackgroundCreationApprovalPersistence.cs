namespace AgentCore.Infrastructure.Security;

internal static class BackgroundCreationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL) OR COALESCE(({Shape}),0)";
    private static string Shape => """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('sessions.create','agent-runs.create') AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.BackgroundStart') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.TenantId') IS lower(TenantId)
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.Permission') IS Permission
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Objective') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Objective')) BETWEEN 1 AND 4000
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Title') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Title')) BETWEEN 1 AND 80
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ReportCompletion') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ConfigurationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model') IS 'object'
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.CatalogKey')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ProviderAlias')) BETWEEN 1 AND 128
        AND json_type(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.Model.ModelId')) BETWEEN 1 AND 128
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        """ + Ids + string.Concat(new[] { "Persona", "Preferences", "CredentialCheck", "SandboxCommand", "SandboxControl", "SandboxRead", "BrowserCommand", "CredentialRead", "ArtifactPublication", "ArtifactVerification", "WorkspaceRead", "WorkspaceMutation", "AttachmentRead" }
            .Select(p => $" AND json_extract(PayloadJson, '$.Operation.{p}') IS NULL"));
    private static string Ids
    {
        get
        {
            var fields = new[] { "ChildSessionId", "EntryId", "ActivationId", "RunId", "ResponseId" };
            string Value(string p) => $"json_extract(PayloadJson, '$.Operation.BackgroundStart.{p}')";
            return string.Concat(fields.Select(f => $" AND json_type(PayloadJson, '$.Operation.BackgroundStart.{f}') IS 'text' AND length({Value(f)}) = 36 AND {Value(f)} NOT GLOB '*[^0-9a-f-]*' AND substr({Value(f)},9,1) IS '-' AND substr({Value(f)},14,1) IS '-' AND substr({Value(f)},19,1) IS '-' AND substr({Value(f)},24,1) IS '-' AND {Value(f)} IS NOT SessionId AND {Value(f)} IS NOT lower(TenantId)"))
                + string.Concat(fields.SelectMany((f, i) => fields.Skip(i + 1).Select(other => $" AND {Value(f)} IS NOT {Value(other)}")));
        }
    }
}
