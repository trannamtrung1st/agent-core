namespace AgentCore.Infrastructure.Security;

internal static class AccountReadApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL) OR COALESCE(({Shape}),0)";
    internal const string Shape = """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId IS CredentialId
        AND OriginalInitiatorPrincipalId IS NOT NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.AccountRead') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.AccountRead.SessionId') IS SessionId
        AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialId') IS CredentialId
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingId') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingId')) = 36
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.Reference') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')) BETWEEN 1 AND 120
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision') >= 1
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') IS 'integer' AND json_extract(PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch') >= CASE WHEN json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 THEN 0 ELSE 1 END
        AND json_type(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson') AS BLOB)) <= 8192
        AND json_valid(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson')) IS 'object'
        AND json_extract(json_extract(PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.accountRef') IS json_extract(PayloadJson, '$.Operation.AccountRead.Credential.Reference')
        AND ((json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 1 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('email.search','email.read'))
          OR (json_extract(PayloadJson, '$.Operation.AccountRead.Purpose') = 2 AND json_extract(PayloadJson, '$.Operation.AccountRead.ToolName') IN ('drive.list','drive.search','drive.get')))
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
        """;
}
