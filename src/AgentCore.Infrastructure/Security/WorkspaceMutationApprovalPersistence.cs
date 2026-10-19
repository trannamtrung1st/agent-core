namespace AgentCore.Infrastructure.Security;

internal static class WorkspaceMutationApprovalPersistence
{
    internal static string Extend(string prior) => $"(({prior}) AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL) OR COALESCE(({Shape}), 0)";

    private const string Shape = """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
        AND Permission IN ('workspaces.write','workspaces.delete') AND ResourceType = 'workspace'
        AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation') IS 'object'
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.SessionId') IS SessionId
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.Working') IN ('true','false')
        AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Permission') IS Permission
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit')
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch'
            OR Permission IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') IN ('workspace.move','workspace.delete') THEN 'workspaces.delete' ELSE 'workspaces.write' END)
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') IS 'text'
        AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 65536
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson')) IS 'object'
        AND (json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName') = 'workspace.batch' OR (
            CASE json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ToolName')
                WHEN 'workspace.copy' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.destination')
                WHEN 'workspace.move' THEN json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.source')
                ELSE json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ArgumentsJson'), '$.path') END
            LIKE CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceMutation.Working') = 1 THEN '/working/%' ELSE '/home/%' END))
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash')) = 64
        AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation.ObservationHash') NOT GLOB '*[^0-9a-f]*'
        AND json_type(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson') IS 'text'
        AND length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 2 AND 4096
        AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson'))
        AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) IS 'array'
        AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceMutation.TargetsJson')) BETWEEN 1 AND 6
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
        AND length(CAST(PayloadJson AS BLOB)) <= 524288
        """;
}
