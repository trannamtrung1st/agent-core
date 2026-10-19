namespace AgentCore.Infrastructure.Security;

internal static class AttachmentReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL) OR COALESCE(({Shape}),0)";
    private const string Shape = """
        Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'attachments.read' AND ResourceType = 'attachment'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AttachmentRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.AttachmentId') IS ResourceId
        AND length(ResourceId) = 36 AND ResourceId NOT GLOB '*[^0-9a-f-]*'
        AND substr(ResourceId,9,1) IS '-' AND substr(ResourceId,14,1) IS '-' AND substr(ResourceId,19,1) IS '-' AND substr(ResourceId,24,1) IS '-'
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.EntryId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.EntryId')) = 36
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.DisplayName') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.DisplayName')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ContentType') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ContentType')) BETWEEN 1 AND 200
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ByteSize') IS 'integer'
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.ByteSize') BETWEEN 0 AND 26214400
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex')) = 64
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead.Sha256Hex') NOT GLOB '*[^0-9a-f]*'
        AND (json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS NULL OR
            (json_type(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion') IS 'text'
                AND length(json_extract(PayloadJson, '$.Operation.AttachmentRead.ProcessorVersion')) BETWEEN 1 AND 128))
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.Vision') IN ('true','false')
        AND json_type(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson') AS BLOB)) <= 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AttachmentRead.ArgumentsJson'), '$.attachmentId') IS ResourceId
        AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL
        AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL
        AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        """;
}
