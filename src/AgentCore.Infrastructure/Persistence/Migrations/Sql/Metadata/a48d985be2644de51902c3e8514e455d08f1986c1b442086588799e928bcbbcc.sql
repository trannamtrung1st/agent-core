((((((((((((((((((((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
    OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-36) NOT GLOB '*[^0-9a-f-]*'
AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-28,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-23,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-18,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),-13,1) IS '-'
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','office.document.inspect','office.spreadsheet.inspect')
AND (substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
    OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/'))) AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId AND (json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'true' OR json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false') AND ((Permission = 'workspaces.read' AND ResourceId IS CASE WHEN json_extract(PayloadJson, '$.Operation.WorkspaceRead.Working') = 1 THEN 'working:' || SessionId ELSE 'home:' || InstanceId END) OR (Permission = 'resources.read' AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'))) AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'text' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IN ('workspace.read','workspace.list','workspace.search','office.document.inspect','office.spreadsheet.inspect') AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384 AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IS 'text' AND length(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path')) BETWEEN 1 AND 4095 AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash')) = 64 AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*' AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL)) AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND length(CAST(PayloadJson AS BLOB)) <= 524288), 0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND (json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path') IN ('/agent/resources','/agent/instance/resources')
    OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,17) IS '/agent/resources/'
    OR substr(json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.path'),1,26) IS '/agent/instance/resources/')
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') AS BLOB)) <= 65536
AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson'))
AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) IS 'array'
AND json_array_length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson')) <= 257
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission = 'resources.read' AND ResourceType = 'resource'
AND ResourceId IS InstanceId || ':' || json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')
AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
AND json_type(PayloadJson, '$.Operation.WorkspaceRead') IS 'object'
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.SessionId') IS SessionId
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Working') IS 'false'
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ToolName') IS 'knowledge.retrieve'
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS 'text'
AND ((length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 45 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,9) IS 'instance:')
    OR (length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey')) = 47 AND substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey'),1,11) IS 'definition:'))
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS 'integer'
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') >= 0
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceMembersJson') IS NULL
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson') AS BLOB)) <= 16384
AND json_valid(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'))
AND json_type(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity') IS 'text'
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge') IS 'object'
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity')) BETWEEN 1 AND 240
AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Identity') IS json_extract(json_extract(PayloadJson, '$.Operation.WorkspaceRead.ArgumentsJson'), '$.identity')
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Title')) BETWEEN 1 AND 512
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Citation')) BETWEEN 1 AND 1024
AND json_type(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path') AS BLOB)) BETWEEN 1 AND 4095
AND (substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,17) IS '/agent/resources/'
    OR substr(json_extract(PayloadJson, '$.Operation.WorkspaceRead.Knowledge.Path'),1,26) IS '/agent/instance/resources/')
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId IS SessionId
AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object'
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source') IS 'object'
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IN ('true','false')
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') AS BLOB)) BETWEEN 7 AND 4095
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.Working') IS 'true'
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceKey') IS NULL
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ResourceRevision') IS NULL
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')) = 36
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') NOT GLOB '*[^0-9a-f-]*'
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId') <> '00000000-0000-0000-0000-000000000000'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),9,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),14,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),19,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),24,1) IS '-'
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentName')) BETWEEN 1 AND 200
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId')) = 36
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId') <> '00000000-0000-0000-0000-000000000000'
AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId'),'-','') NOT GLOB '*[^0-9a-f]*'
AND length(replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','')) = 32 AND replace(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),'-','') NOT GLOB '*[^0-9a-f]*'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),9,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),14,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),19,1) IS '-'
AND substr(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.EntryId'),24,1) IS '-'
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.Path') IS '/attachments/' || json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.AttachmentId')
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ContentType')) BETWEEN 1 AND 200
AND json_type(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') IS 'integer'
AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.Source.ByteSize') BETWEEN 0 AND 26214400
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
AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.AttachmentMembersJson') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission IN ('sessions.create','agent-runs.create') AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
AND OriginalInitiatorPrincipalId IS NOT NULL
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ChildSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.EntryId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundStart.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundStart.ResponseId') AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission = 'agent-runs.create' AND ResourceType = 'tenant' AND ResourceId IS lower(TenantId)
AND OriginalInitiatorPrincipalId IS NOT NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072 AND json_type(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.RunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT lower(TenantId) AND json_type(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId')) = 36 AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT SessionId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT RunId AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') IS NOT lower(TenantId) AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ParentSessionId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.ActivationId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_extract(PayloadJson, '$.Operation.BackgroundReport.RunId') IS NOT json_extract(PayloadJson, '$.Operation.BackgroundReport.ResponseId') AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.CatalogKey')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ProviderAlias')) BETWEEN 1 AND 128 AND json_type(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BackgroundReport.Model.ModelId')) BETWEEN 1 AND 128 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceMutation') IS NULL AND json_extract(PayloadJson, '$.Operation.AttachmentRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BackgroundStart') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run'
AND ResourceId IS json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')
AND OriginalInitiatorPrincipalId IS NOT NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
AND json_type(PayloadJson, '$.Operation.CompletionRead') IS 'object'
AND json_extract(PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS SessionId
AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
AND json_type(PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS SessionId
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS ResourceId
AND json_extract(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
AND json_type(PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
AND json_extract(PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
AND length(CAST(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
AND json_valid(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
AND json_type(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
AND json_array_length(json_extract(PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
AND length(CAST(PayloadJson AS BLOB)) <= 131072
AND json_type(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT SessionId
AND json_type(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT SessionId
AND json_type(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT SessionId
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
AND json_extract(PayloadJson, '$.Operation.BackgroundReport') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL) OR COALESCE((Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND Permission = 'agent-runs.read' AND ResourceType = 'agent-run' AND ResourceId IS RunId
AND OriginalInitiatorPrincipalId IS NOT NULL AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
AND json_type(PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS SessionId
AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS RunId
AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT SessionId
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
AND json_valid(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
AND json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
AND json_type(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
AND json_valid(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS SessionId
AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS RunId
AND json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
AND json_array_length(json_extract(json_extract(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
AND length(CAST(PayloadJson AS BLOB)) <= 131072
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
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
AND json_extract(PayloadJson, '$.Operation.CompletionRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
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
AND json_extract(PayloadJson, '$.Operation.ReportSourceRead') IS NULL),0)) AND json_extract(PayloadJson, '$.Operation.AccountAction') IS NULL) OR COALESCE((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64
AND CredentialId IS NOT NULL
AND json_extract(PayloadJson, '$.Operation.AccountAction.Permission') IS Permission
AND ((Permission IN ('email.draft','email.send','drive.write','drive.delete') AND ResourceType = 'credential' AND ResourceId IS CredentialId)
  OR (Permission = 'workspaces.write' AND ResourceType = 'workspace' AND ResourceId IS CASE
    WHEN substr(json_extract(json_extract(PayloadJson, '$.Operation.AccountAction.ArgumentsJson'),'$.path'),1,9) = '/working/' THEN 'working:' || SessionId ELSE 'home:' || InstanceId END))
AND OriginalInitiatorPrincipalId IS NOT NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL
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
AND json_extract(PayloadJson, '$.Operation.AccountRead') IS NULL),0)