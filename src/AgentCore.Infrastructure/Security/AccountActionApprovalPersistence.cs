namespace AgentCore.Infrastructure.Security;

internal static class AccountActionApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL) OR COALESCE(({Shape}),0)";
    internal const string Shape = """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND CredentialId IS NOT NULL
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Permission') IS Permission
        AND ((Permission IN ('email.draft','email.send','drive.write','drive.delete') AND ResourceType = 'credential' AND ResourceId IS CredentialId)
          OR (Permission = 'workspaces.write' AND ResourceType = 'workspace' AND ResourceId IS CASE
            WHEN substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END))
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountAction') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountAction.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson') AS BLOB)) <= 49152
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountAction.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 1
            AND ((Permission = 'email.draft' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.create_draft')
              OR (Permission = 'email.send' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'email.send'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft') IS 'object'
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.DraftId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.DraftId')) BETWEEN 1 AND 200
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.RecipientsJson'))
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Subject') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Subject')) <= 500
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Body') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Body')) <= 32000
            AND json_type(PayloadJson, '$.Operation.AccountAction.Draft.Sender') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Sender')) BETWEEN 3 AND 320
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft.Revision') >= 1
            AND json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') = '[]'
            AND json_extract(PayloadJson, '$.Operation.AccountAction.UploadSource') IS NULL AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS NULL)
          OR (json_extract(PayloadJson, '$.Operation.AccountAction.Purpose') = 2
            AND json_extract(PayloadJson, '$.Operation.AccountAction.Draft') IS NULL
            AND ((Permission = 'drive.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.create_folder','drive.upload','drive.rename','drive.move'))
              OR (Permission = 'drive.delete' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') IN ('drive.trash','drive.trash_batch'))
              OR (Permission = 'workspaces.write' AND json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') = 'drive.download'))))
        AND json_type(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson') AS BLOB)) <= 32768
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.AccountAction.RemoteSourcesJson')) <= 21
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.upload' OR json_type(PayloadJson, '$.Operation.AccountAction.UploadSource') IS 'object')
        AND (json_extract(PayloadJson, '$.Operation.AccountAction.ToolName') <> 'drive.download' OR (json_type(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') IS 'text'
            AND length(json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.AccountAction.DestinationObservationHash') NOT GLOB '*[^0-9a-f]*'))
        AND length(CAST(PayloadJson AS BLOB)) <= 131072
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
        AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL
        AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL
        AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL
        AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL
        """;
}
