((((((((((((((Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576) OR (Permission = 'sandbox.manage' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxControl.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.read' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.SandboxRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.SandboxRead.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.SandboxRead.Kind') IN (0,1,2) AND json_type(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.SandboxRead.ProfileReferencesJson')) <= 18 AND length(PayloadJson) <= 131072) OR (Permission IN ('browser.read','browser.use') AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.BrowserCommand') IS 'object' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'text' AND json_type(PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS CASE WHEN Permission = 'browser.read' THEN 'true' ELSE 'false' END AND json_type(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')) <= 16384 AND json_type(PayloadJson, '$.Operation.BrowserCommand.TargetJson') IS 'text' AND json_valid(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.TargetJson')) <= 4096 AND json_type(PayloadJson, '$.Operation.BrowserCommand.CommandHash') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.BrowserCommand.CommandHash')) = 64 AND length(PayloadJson) <= 262144) OR (Kind = 0 AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.CredentialRead') IS 'object' AND json_extract(PayloadJson, '$.Operation.CredentialRead.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) <= 4096 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ArgumentsJson')) AND json_type(PayloadJson, '$.Operation.CredentialRead.ReferencesJson') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) AND json_type(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) IS 'array' AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) <= 101 AND ((Permission = 'credentials.read' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS CredentialId AND json_extract(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson'), '$[0].CredentialId') IS CredentialId) OR (Permission = 'sessions.read' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead.CredentialId') IS NULL AND json_array_length(json_extract(PayloadJson, '$.Operation.CredentialRead.ReferencesJson')) = 0)) AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND length(PayloadJson) <= 262144))) AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL) OR (Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.create' AND ResourceType = 'session' AND ResourceId = SessionId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactPublication') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactPublication.SessionId') IS SessionId AND json_type(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') IS 'text' AND length(CAST(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson') AS BLOB)) <= 65536 AND json_valid(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson')) IS 'object' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.displayName') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.contentType') IS 'text' AND json_type(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') IS 'text' AND length(CAST(json_extract(json_extract(PayloadJson, '$.Operation.ArtifactPublication.ArgumentsJson'), '$.content') AS BLOB)) <= 32768 AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 524288)) AND json_extract(PayloadJson, '$.Operation.ArtifactVerification') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'artifacts.read' AND ResourceType = 'artifact' AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND json_type(PayloadJson, '$.Operation.ArtifactVerification') IS 'object' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.SessionId') IS SessionId AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ArtifactId') IS ResourceId AND json_type(PayloadJson, '$.Operation.ArtifactVerification.DisplayName') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.DisplayName')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ContentType') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.ContentType')) BETWEEN 1 AND 200 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') IS 'integer' AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.ByteSize') >= 0 AND json_type(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') IS 'text' AND length(json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex')) = 64 AND json_extract(PayloadJson, '$.Operation.ArtifactVerification.Sha256Hex') NOT GLOB '*[^0-9a-f]*' AND json_extract(PayloadJson, '$.Operation.Persona') IS NULL AND json_extract(PayloadJson, '$.Operation.Preferences') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialCheck') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxControl') IS NULL AND json_extract(PayloadJson, '$.Operation.SandboxRead') IS NULL AND json_extract(PayloadJson, '$.Operation.BrowserCommand') IS NULL AND json_extract(PayloadJson, '$.Operation.CredentialRead') IS NULL AND json_extract(PayloadJson, '$.Operation.ArtifactPublication') IS NULL AND length(CAST(PayloadJson AS BLOB)) <= 131072)) AND json_extract(PayloadJson, '$.Operation.WorkspaceRead') IS NULL) OR (Kind = 0 AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'workspaces.read' AND ResourceType = 'workspace' AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceKey') IS NULL AND json_extract(PayloadJson, '$.Operation.WorkspaceRead.ResourceRevision') IS NULL) OR (Permission = 'resources.read' AND ResourceType = 'resource'
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
AND length(CAST(PayloadJson AS BLOB)) <= 131072),0)