namespace AgentCore.Infrastructure.Security;

internal static class ArtifactSourceApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source') IS NULL) OR COALESCE(({Shape}), 0)";
    private const string Shape = """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
        AND ((json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
            AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
            AND CASE WHEN json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') = 1
            THEN substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,9) IS '/working/'
            ELSE substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,6) IS '/home/' END)
            OR (json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'false'
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS 'text'
                AND ((length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,9) IS 'instance:')
                    OR (length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey'),1,11) IS 'definition:'))
                AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS 'integer'
                AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') >= 0
                AND (substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,17) IS '/agent/resources/'
                    OR substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path'),1,26) IS '/agent/instance/resources/')))
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 52428800
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object'
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.path') IS json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path')
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text'
        AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName')) BETWEEN 1 AND 200
        AND (json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS NULL OR (
            json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text'
            AND length(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType')) BETWEEN 1 AND 200))
        AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 524288
        """;
}
