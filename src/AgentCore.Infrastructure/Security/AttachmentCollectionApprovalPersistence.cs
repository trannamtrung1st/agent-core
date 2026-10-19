namespace AgentCore.Infrastructure.Security;

internal static class AttachmentCollectionApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS NULL) OR COALESCE(({Shape}),0)";
    private const string Shape = """
        Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId IS SessionId
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.list','workspace.search')
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text'
        AND json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS '/attachments'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') AS BLOB)) <= 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson')) <= 257
        AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
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
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
        """;
}
