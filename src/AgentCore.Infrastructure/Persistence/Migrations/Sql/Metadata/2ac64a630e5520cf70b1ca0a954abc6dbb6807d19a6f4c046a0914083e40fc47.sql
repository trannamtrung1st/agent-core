CREATE TRIGGER TR_AgentDecommission_IdentityDelete BEFORE DELETE ON SystemIdentities
WHEN OLD.Kind=0 AND OLD.TenantId IS NOT NULL AND OLD.Status=2
BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent ownership is retained'); END;
CREATE TRIGGER TR_AgentDecommission_IdentityUpdate BEFORE UPDATE ON SystemIdentities
WHEN OLD.Kind=0 AND OLD.TenantId IS NOT NULL AND OLD.Status=2
AND (NEW.Status IS NOT 2 OR NEW.AgentInstanceId IS NOT OLD.AgentInstanceId OR NEW.PrincipalId IS NOT OLD.PrincipalId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.Kind IS NOT OLD.Kind)
BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent ownership is retained'); END;
CREATE TRIGGER TR_AgentDecommission_Instance BEFORE UPDATE ON AgentInstances
WHEN NEW.Lifecycle<>'Archived' AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.AgentInstanceId=OLD.InstanceId AND i.Kind=0 AND i.Status=2)
BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
CREATE TRIGGER TR_AgentDecommission_Principal BEFORE UPDATE ON Principals
WHEN NEW.Status<>2 AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.PrincipalId=OLD.PrincipalId AND i.Kind=0 AND i.Status=2)
BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
CREATE TRIGGER TR_AgentInstances_ProfileReset_Insert BEFORE INSERT ON AgentInstances
WHEN NEW.BrowserProfileResetJson IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'Profile reset requires an existing owned Instance'); END;
CREATE TRIGGER TR_AgentInstances_ProfileReset_Update BEFORE UPDATE ON AgentInstances
WHEN OLD.BrowserProfileResetJson IS NOT NEW.BrowserProfileResetJson
BEGIN
SELECT RAISE(ABORT, 'Invalid owned Browser profile reset') WHERE NOT COALESCE((
NEW.BrowserProfileResetJson IS NOT NULL AND length(CAST(NEW.BrowserProfileResetJson AS BLOB)) <= 4096
AND json_valid(NEW.BrowserProfileResetJson) AND json_type(NEW.BrowserProfileResetJson) = 'object'
AND (SELECT count(*) FROM json_each(NEW.BrowserProfileResetJson)) = 8
AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserProfileResetJson)
WHERE key IN ('operationId','principalId','providerEpoch','witness','status','reasonCode','requestedAt','recordedAt')) = 8
AND json_type(NEW.BrowserProfileResetJson, '$.operationId') = 'text'
AND length(json_extract(NEW.BrowserProfileResetJson, '$.operationId')) = 36
AND json_extract(NEW.BrowserProfileResetJson, '$.operationId') NOT GLOB '*[^0-9a-f-]*'
AND length(replace(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),'-','')) = 32
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),9,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),14,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),19,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),24,1) = '-'
AND json_extract(NEW.BrowserProfileResetJson, '$.operationId') <> '00000000-0000-0000-0000-000000000000'
AND json_type(NEW.BrowserProfileResetJson, '$.providerEpoch') = 'text'
AND length(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch')) = 36
AND json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch') NOT GLOB '*[^0-9a-f-]*'
AND length(replace(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),'-','')) = 32
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),9,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),14,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),19,1) = '-'
AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),24,1) = '-'
AND json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch') <> '00000000-0000-0000-0000-000000000000'
AND json_type(NEW.BrowserProfileResetJson, '$.witness') = 'text'
AND length(json_extract(NEW.BrowserProfileResetJson, '$.witness')) = 64
AND json_extract(NEW.BrowserProfileResetJson, '$.witness') NOT GLOB '*[^0-9a-f]*'
AND json_type(NEW.BrowserProfileResetJson, '$.requestedAt') = 'text'
AND julianday(json_extract(NEW.BrowserProfileResetJson, '$.requestedAt')) IS NOT NULL
AND NEW.Revision = OLD.Revision + 1
AND EXISTS (SELECT 1 FROM SystemIdentities i JOIN TenantUsers u ON u.TenantId = i.TenantId
WHERE lower(i.AgentInstanceId) = lower(NEW.InstanceId) AND i.Kind = 0 AND i.TenantId IS NOT NULL
AND lower(u.PrincipalId) = json_extract(NEW.BrowserProfileResetJson, '$.principalId'))
AND ((json_extract(NEW.BrowserProfileResetJson, '$.status') = 'pending'
AND json_extract(NEW.BrowserProfileResetJson, '$.reasonCode') = 'browser_profile_reset_started'
AND json_type(NEW.BrowserProfileResetJson, '$.recordedAt') = 'null'
AND (OLD.BrowserProfileResetJson IS NULL OR json_extract(OLD.BrowserProfileResetJson, '$.status') IN ('reset','unknown'))
AND (OLD.BrowserProfileResetJson IS NULL OR json_extract(OLD.BrowserProfileResetJson, '$.operationId') IS NOT json_extract(NEW.BrowserProfileResetJson, '$.operationId')))
OR (OLD.BrowserProfileResetJson IS NOT NULL AND json_extract(OLD.BrowserProfileResetJson, '$.status') = 'pending'
AND json_extract(NEW.BrowserProfileResetJson, '$.status') IN ('reset','unknown')
AND json_extract(NEW.BrowserProfileResetJson, '$.reasonCode') = CASE json_extract(NEW.BrowserProfileResetJson, '$.status')
WHEN 'reset' THEN 'browser_profile_reset_completed' ELSE 'browser_profile_reset_unknown' END
AND json_type(NEW.BrowserProfileResetJson, '$.recordedAt') = 'text'
AND julianday(json_extract(NEW.BrowserProfileResetJson, '$.recordedAt')) >= julianday(json_extract(NEW.BrowserProfileResetJson, '$.requestedAt'))
AND json_extract(OLD.BrowserProfileResetJson, '$.operationId') IS json_extract(NEW.BrowserProfileResetJson, '$.operationId')
AND json_extract(OLD.BrowserProfileResetJson, '$.principalId') IS json_extract(NEW.BrowserProfileResetJson, '$.principalId')
AND json_extract(OLD.BrowserProfileResetJson, '$.providerEpoch') IS json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch')
AND json_extract(OLD.BrowserProfileResetJson, '$.witness') IS json_extract(NEW.BrowserProfileResetJson, '$.witness')
AND json_extract(OLD.BrowserProfileResetJson, '$.requestedAt') IS json_extract(NEW.BrowserProfileResetJson, '$.requestedAt')))
), 0);
END;
CREATE TRIGGER TR_AgentRuns_DirectWorkspace_Insert
BEFORE INSERT ON AgentRuns
BEGIN
SELECT RAISE(ABORT, 'invalid direct workspace operation') WHERE NOT (
json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS NULL
OR (NEW.TenantId IS NOT NULL AND NEW.ExecutorPrincipalId IS NOT NULL
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId')) = 36
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') <> '00000000-0000-0000-0000-000000000000'
AND json_extract(NEW.PayloadJson, '$.sideEffect.toolCallId') IS ('operation:' || json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId'))
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId')) BETWEEN 1 AND 200
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IS 'text'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit','web.download')
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') AS BLOB)) BETWEEN 2 AND 65536
AND json_valid(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson')) IS 'object'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash')) = 64
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') >= 1
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') >= 1)
);
END;
CREATE TRIGGER TR_AgentRuns_DirectWorkspace_Update
BEFORE UPDATE ON AgentRuns
BEGIN
SELECT RAISE(ABORT, 'invalid direct workspace operation') WHERE NOT (
json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS NULL
OR (NEW.TenantId IS NOT NULL AND NEW.ExecutorPrincipalId IS NOT NULL
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation') IS 'object'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId')) = 36
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId') <> '00000000-0000-0000-0000-000000000000'
AND json_extract(NEW.PayloadJson, '$.sideEffect.toolCallId') IS ('operation:' || json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.operationId'))
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.sourceCallId')) BETWEEN 1 AND 200
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IS 'text'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.toolName') IN ('workspace.write','workspace.patch','workspace.mkdir','workspace.copy','workspace.move','workspace.delete','workspace.batch','office.document.create','office.document.edit','office.spreadsheet.create','office.spreadsheet.edit','web.download')
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson') AS BLOB)) BETWEEN 2 AND 65536
AND json_valid(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.argumentsJson')) IS 'object'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash')) = 64
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.parametersHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.instanceRevision') >= 1
AND json_type(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.sideEffect.externalEffect.directOperation.personaRevision') >= 1)
);
END;
CREATE TRIGGER TR_AgentRuns_NoEmbeddedApproval_INSERT BEFORE INSERT ON AgentRuns
WHEN json_type(NEW.PayloadJson,'$.approval') IS NOT NULL OR (NEW.Status=2 AND NEW.TenantId IS NULL)
BEGIN SELECT RAISE(ABORT,'embedded Run approvals are retired; use canonical tenant requests'); END;
CREATE TRIGGER TR_AgentRuns_NoEmbeddedApproval_UPDATE BEFORE UPDATE ON AgentRuns
WHEN json_type(NEW.PayloadJson,'$.approval') IS NOT NULL OR (NEW.Status=2 AND NEW.TenantId IS NULL)
BEGIN SELECT RAISE(ABORT,'embedded Run approvals are retired; use canonical tenant requests'); END;
CREATE TRIGGER TR_Artifacts_TenantRun_Insert BEFORE INSERT ON Artifacts
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'owned Artifact requires actual provenance')
WHERE NOT (
(NEW.AgentRunId IS NOT NULL AND EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId
AND r.AgentRunId = NEW.AgentRunId AND r.SessionId = NEW.SessionId)) OR
(NEW.AgentRunId IS NULL AND EXISTS (SELECT 1 FROM Principals p WHERE p.TenantId = NEW.TenantId
AND p.PrincipalId = NEW.CreatedByPrincipalId AND p.Kind = 0)
AND EXISTS (SELECT 1 FROM Attachments s WHERE s.TenantId = NEW.TenantId AND s.SessionId = NEW.SessionId
AND s.AttachmentId = NEW.SourceAttachmentId AND s.ByteSize = NEW.ByteSize AND s.Sha256Hex = NEW.Sha256Hex
AND s.ContentType = NEW.ContentType))
);
END;
CREATE TRIGGER TR_Artifacts_TenantRun_Update BEFORE UPDATE ON Artifacts
WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'owned Artifact is immutable');
END;
CREATE TRIGGER TR_Attachments_Tenant_Insert BEFORE INSERT ON Attachments
WHEN (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
OR (NEW.TenantId IS NOT NULL AND (NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
BEGIN SELECT RAISE(ABORT, 'Attachment ownership mismatch'); END;
CREATE TRIGGER TR_Attachments_Tenant_Update BEFORE UPDATE ON Attachments
WHEN OLD.TenantId IS NOT NEW.TenantId
OR (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
OR (OLD.TenantId IS NOT NULL AND (OLD.SessionId IS NOT NEW.SessionId OR OLD.AttachmentId IS NOT NEW.AttachmentId
OR OLD.UploadRequestId IS NOT NEW.UploadRequestId OR OLD.UploadedByPrincipalId IS NOT NEW.UploadedByPrincipalId
OR OLD.BlobKey IS NOT NEW.BlobKey OR OLD.DisplayName IS NOT NEW.DisplayName OR OLD.ContentType IS NOT NEW.ContentType
OR OLD.ByteSize IS NOT NEW.ByteSize OR OLD.Sha256Hex IS NOT NEW.Sha256Hex OR OLD.Readable IS NOT NEW.Readable OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
OR (OLD.State = 'Bound' AND (NEW.State IS NOT 'Bound' OR OLD.EntryId IS NOT NEW.EntryId OR OLD.BoundAtUtc IS NOT NEW.BoundAtUtc))
OR (OLD.State = 'Pending' AND NEW.State = 'Pending' AND NEW.ExpiresAtUtc > OLD.ExpiresAtUtc)
OR NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
BEGIN SELECT RAISE(ABORT, 'Attachment ownership or immutable source mismatch'); END;
CREATE TRIGGER TR_BackgroundReceipt_OwnerInsert BEFORE INSERT ON BackgroundCompletionReceipts
WHEN NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.AgentRunId=NEW.ChildAgentRunId AND r.AgentInstanceId=NEW.AgentInstanceId
AND ((r.TenantId IS NOT NULL AND NEW.ProfileId IS NULL) OR (r.TenantId IS NULL AND r.ProfileId=NEW.ProfileId)))
OR (NEW.ParentActivationId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Activations a JOIN AgentRuns r ON r.AgentRunId=NEW.ChildAgentRunId
WHERE a.ActivationId=NEW.ParentActivationId AND a.SessionId=NEW.ParentSessionId AND a.AgentInstanceId=r.AgentInstanceId
AND a.TenantId IS r.TenantId AND a.ExecutorPrincipalId IS r.ExecutorPrincipalId AND a.ProfileId IS r.ProfileId AND a.RootActivationId=r.RootActivationId))
BEGIN SELECT RAISE(ABORT, 'Background receipt owner mismatch'); END;
CREATE TRIGGER TR_BackgroundReceipt_OwnerUpdate BEFORE UPDATE ON BackgroundCompletionReceipts
WHEN NEW.ChildAgentRunId<>OLD.ChildAgentRunId OR NEW.AgentInstanceId<>OLD.AgentInstanceId OR NEW.ProfileId IS NOT OLD.ProfileId
OR NEW.ParentSessionId<>OLD.ParentSessionId
OR (NEW.ParentActivationId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM Activations a JOIN AgentRuns r ON r.AgentRunId=NEW.ChildAgentRunId
WHERE a.ActivationId=NEW.ParentActivationId AND a.SessionId=NEW.ParentSessionId AND a.AgentInstanceId=r.AgentInstanceId
AND a.TenantId IS r.TenantId AND a.ExecutorPrincipalId IS r.ExecutorPrincipalId AND a.ProfileId IS r.ProfileId AND a.RootActivationId=r.RootActivationId))
BEGIN SELECT RAISE(ABORT, 'Background receipt owner mismatch'); END;
CREATE TRIGGER TR_BrowserPrivacy_System_Delete BEFORE DELETE ON BrowserPrivacy BEGIN SELECT RAISE(ABORT, 'System Browser privacy is retained'); END;
CREATE TRIGGER TR_BrowserPrivacy_System_INSERT BEFORE INSERT ON BrowserPrivacy
BEGIN
SELECT RAISE(ABORT, 'Invalid system Browser privacy policy') WHERE NOT COALESCE((
NEW.Id = 1 AND NEW.Revision = 1 AND NEW.Revision > 0
AND length(CAST(NEW.PolicyJson AS BLOB)) <= 40960 AND json_valid(NEW.PolicyJson)
AND json_type(NEW.PolicyJson) = 'object' AND (SELECT count(*) FROM json_each(NEW.PolicyJson)) = 4
AND (SELECT count(DISTINCT key) FROM json_each(NEW.PolicyJson) WHERE key IN ('Mode','Revision','UnmaskedOrigins','TrustedGraphicsOrigins')) = 4
AND json_type(NEW.PolicyJson,'$.Mode') = 'integer' AND json_extract(NEW.PolicyJson,'$.Mode') IN (0,1,2)
AND json_type(NEW.PolicyJson,'$.Revision') = 'integer' AND json_extract(NEW.PolicyJson,'$.Revision') = NEW.Revision
AND json_type(NEW.PolicyJson,'$.UnmaskedOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') <= 32
AND json_type(NEW.PolicyJson,'$.TrustedGraphicsOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.TrustedGraphicsOrigins') <= 32
AND (json_extract(NEW.PolicyJson,'$.Mode') = 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') > 0
OR json_extract(NEW.PolicyJson,'$.Mode') <> 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') = 0)
AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PolicyJson) arrays, json_each(arrays.value) origin
WHERE arrays.key IN ('UnmaskedOrigins','TrustedGraphicsOrigins') AND (origin.type <> 'text' OR length(origin.value) > 512
OR NOT (origin.value LIKE 'http://_%' OR origin.value LIKE 'https://_%') OR instr(substr(origin.value,instr(origin.value,'://')+3),'/') > 0
OR instr(origin.value,'*') > 0 OR instr(origin.value,char(92)) > 0 OR instr(origin.value,'?') > 0 OR instr(origin.value,'#') > 0 OR instr(origin.value,'@') > 0))
AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins'))
AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins'))
),0);
END;
CREATE TRIGGER TR_BrowserPrivacy_System_UPDATE BEFORE UPDATE ON BrowserPrivacy
BEGIN
SELECT RAISE(ABORT, 'Invalid system Browser privacy policy') WHERE NOT COALESCE((
NEW.Id = 1 AND NEW.Id = OLD.Id AND NEW.Revision = OLD.Revision + 1 AND NEW.Revision > 0
AND length(CAST(NEW.PolicyJson AS BLOB)) <= 40960 AND json_valid(NEW.PolicyJson)
AND json_type(NEW.PolicyJson) = 'object' AND (SELECT count(*) FROM json_each(NEW.PolicyJson)) = 4
AND (SELECT count(DISTINCT key) FROM json_each(NEW.PolicyJson) WHERE key IN ('Mode','Revision','UnmaskedOrigins','TrustedGraphicsOrigins')) = 4
AND json_type(NEW.PolicyJson,'$.Mode') = 'integer' AND json_extract(NEW.PolicyJson,'$.Mode') IN (0,1,2)
AND json_type(NEW.PolicyJson,'$.Revision') = 'integer' AND json_extract(NEW.PolicyJson,'$.Revision') = NEW.Revision
AND json_type(NEW.PolicyJson,'$.UnmaskedOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') <= 32
AND json_type(NEW.PolicyJson,'$.TrustedGraphicsOrigins') = 'array' AND json_array_length(NEW.PolicyJson,'$.TrustedGraphicsOrigins') <= 32
AND (json_extract(NEW.PolicyJson,'$.Mode') = 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') > 0
OR json_extract(NEW.PolicyJson,'$.Mode') <> 1 AND json_array_length(NEW.PolicyJson,'$.UnmaskedOrigins') = 0)
AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PolicyJson) arrays, json_each(arrays.value) origin
WHERE arrays.key IN ('UnmaskedOrigins','TrustedGraphicsOrigins') AND (origin.type <> 'text' OR length(origin.value) > 512
OR NOT (origin.value LIKE 'http://_%' OR origin.value LIKE 'https://_%') OR instr(substr(origin.value,instr(origin.value,'://')+3),'/') > 0
OR instr(origin.value,'*') > 0 OR instr(origin.value,char(92)) > 0 OR instr(origin.value,'?') > 0 OR instr(origin.value,'#') > 0 OR instr(origin.value,'@') > 0))
AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.UnmaskedOrigins'))
AND (SELECT count(*) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins')) = (SELECT count(DISTINCT value COLLATE NOCASE) FROM json_each(NEW.PolicyJson,'$.TrustedGraphicsOrigins'))
),0);
END;
CREATE TRIGGER TR_Credentials_ModelInferencePurpose_UPDATE BEFORE UPDATE ON Credentials
WHEN EXISTS(SELECT 1 FROM ProviderConnections p WHERE p.CredentialId=OLD.CredentialId)
    AND (NEW.CredentialId<>OLD.CredentialId OR NEW.Purpose<>'ModelInference' OR NEW.Kind<>'ApiKey')
BEGIN SELECT RAISE(ABORT,'referenced model credential purpose and identity are immutable'); END;
CREATE TRIGGER TR_ModelOfferings_Identity_UPDATE BEFORE UPDATE ON ModelOfferings
WHEN NEW.OfferingId<>OLD.OfferingId OR NEW.TenantId<>OLD.TenantId OR NEW.ConnectionId<>OLD.ConnectionId OR NEW.Kind<>OLD.Kind
    OR NEW.ExternalModelId<>OLD.ExternalModelId OR NEW.PresetId IS NOT OLD.PresetId OR OLD.Retired=1 AND NEW.Retired<>1
BEGIN SELECT RAISE(ABORT,'model offering identity and retirement are immutable'); END;
CREATE TRIGGER TR_ModelOfferings_Retain_DELETE BEFORE DELETE ON ModelOfferings
BEGIN SELECT RAISE(ABORT,'retire model offerings without deleting historical identity'); END;
CREATE TRIGGER TR_ProviderConnections_Credential_INSERT BEFORE INSERT ON ProviderConnections WHEN NEW.CredentialId IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'model connection requires an owned inference API credential') WHERE NOT EXISTS
        (SELECT 1 FROM Credentials c JOIN TenantCredentials t ON t.CredentialId=c.CredentialId
        WHERE c.CredentialId=NEW.CredentialId AND t.TenantId=NEW.TenantId AND t.DeletedAt IS NULL AND c.Purpose='ModelInference' AND c.Kind='ApiKey');
END;
CREATE TRIGGER TR_ProviderConnections_Credential_UPDATE BEFORE UPDATE ON ProviderConnections WHEN NEW.CredentialId IS NOT NULL
BEGIN
    SELECT RAISE(ABORT,'model connection requires an owned inference API credential') WHERE NOT EXISTS
        (SELECT 1 FROM Credentials c JOIN TenantCredentials t ON t.CredentialId=c.CredentialId
        WHERE c.CredentialId=NEW.CredentialId AND t.TenantId=NEW.TenantId AND t.DeletedAt IS NULL AND c.Purpose='ModelInference' AND c.Kind='ApiKey');
END;
CREATE TRIGGER TR_ProviderConnections_Identity_UPDATE BEFORE UPDATE ON ProviderConnections
WHEN NEW.ConnectionId<>OLD.ConnectionId OR NEW.TenantId<>OLD.TenantId OR NEW.Kind<>OLD.Kind OR NEW.CreatedAt<>OLD.CreatedAt
BEGIN SELECT RAISE(ABORT,'model connection identity is immutable'); END;
CREATE TRIGGER TR_ProviderConnections_Retain_DELETE BEFORE DELETE ON ProviderConnections
BEGIN SELECT RAISE(ABORT,'retire model connections without deleting historical identity'); END;
CREATE TRIGGER TR_TenantApprovals_AccountAction_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object' AND NOT EXISTS (
SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingId')
AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.Reference')
AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision')
AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision')
AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Credential.SecretRevision')
AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountAction.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
BEGIN SELECT RAISE(ABORT, 'exact account action binding required'); END;
CREATE TRIGGER TR_TenantApprovals_AccountAction_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountAction') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountAction') IS 'object')
AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
BEGIN SELECT RAISE(ABORT, 'account action consent is immutable'); END;
CREATE TRIGGER TR_TenantApprovals_AccountOperationRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
OR json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.operationId') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'canonical operation read source required') WHERE NOT EXISTS (
SELECT 1 FROM TenantApprovalRequests original JOIN TenantOperationReceipts receipt ON receipt.RequestId = original.RequestId
JOIN AgentRuns source ON source.AgentRunId = receipt.RunId
JOIN TenantSessions session ON session.SessionId = receipt.SessionId AND session.TenantId = receipt.TenantId
JOIN Sessions retained ON retained.SessionId = session.SessionId
WHERE original.TenantId = NEW.TenantId AND original.InstanceId = NEW.InstanceId AND original.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND lower(original.RequestId) = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RequestId') AND original.Status = 1
AND lower(original.OperationId) = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.OperationId')
AND original.Fingerprint = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ActionHash')
AND original.SessionId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.SessionId')
AND json_type(original.PayloadJson, '$.Operation.AccountAction') IS 'object'
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.ToolName') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ToolName')
AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.ToolName') IN ('drive.create_folder','drive.upload','drive.download','drive.rename','drive.move','drive.trash','drive.trash_batch')
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.BindingId') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingId')
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.CredentialId') = NEW.CredentialId
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.BindingRevision') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision')
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.CredentialRevision') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision')
AND json_extract(original.PayloadJson, '$.Operation.AccountAction.Credential.AuthEpoch') = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.AuthEpoch')
AND receipt.TenantId = NEW.TenantId AND receipt.InstanceId = NEW.InstanceId AND receipt.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND receipt.OperationId = original.OperationId AND receipt.ActionHash = original.Fingerprint AND receipt.SessionId = original.SessionId
AND receipt.RunId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RunId') AND receipt.RootActivationId = original.RootActivationId
AND CASE receipt.Outcome WHEN 0 THEN 'Completed' WHEN 1 THEN 'Denied' WHEN 2 THEN 'Indeterminate' END = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.RecordedOutcome')
AND source.TenantId = NEW.TenantId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND source.SessionId = receipt.SessionId AND source.RootActivationId = original.RootActivationId
AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND retained.DurablyDeletedAtUtc IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ToolName') IS 'drive.get'
AND json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Purpose') IS 2
AND json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS 'object'
AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.AccountRead.Source')) = 8
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.operationId') = lower(original.OperationId)
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'), '$.fileId') IS NULL
AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.ArgumentsJson'))) = 2
AND json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson') AS BLOB)) <= 16384
AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) IS 'array'
AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) BETWEEN 0 AND 20);
SELECT RAISE(ABORT, 'invalid native operation references') WHERE EXISTS (
SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')) member
WHERE member.type IS NOT 'object' OR (SELECT count(*) FROM json_each(member.value)) <> 4
OR json_type(member.value, '$.OperationId') IS NOT 'text' OR length(json_extract(member.value, '$.OperationId')) <> 36
OR json_extract(member.value, '$.OperationId') GLOB '*[^0-9a-f-]*'
OR COALESCE(json_type(member.value, '$.FileId'),'') NOT IN ('null','text') OR length(json_extract(member.value, '$.FileId')) > 160
OR COALESCE(json_type(member.value, '$.EffectHash'),'') NOT IN ('null','text') OR COALESCE(json_type(member.value, '$.SnapshotHash'),'') NOT IN ('null','text')
OR json_type(member.value, '$.EffectHash') IS NOT json_type(member.value, '$.SnapshotHash')
OR (json_type(member.value, '$.EffectHash') IS 'text' AND (length(json_extract(member.value, '$.EffectHash')) <> 64
OR json_extract(member.value, '$.EffectHash') GLOB '*[^0-9a-f]*' OR length(json_extract(member.value, '$.SnapshotHash')) <> 64
OR json_extract(member.value, '$.SnapshotHash') GLOB '*[^0-9a-f]*')));
SELECT RAISE(ABORT, 'duplicate native operation references') WHERE
(SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson'))) <>
(SELECT count(DISTINCT json_extract(value, '$.OperationId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Source.NativeReferencesJson')));
END;
CREATE TRIGGER TR_TenantApprovals_AccountOperationRead_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_type(OLD.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
OR json_type(NEW.PayloadJson, '$.Operation.AccountRead.Source') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'canonical operation read source is immutable') WHERE
json_extract(OLD.PayloadJson, '$.Operation.AccountRead') IS NOT json_extract(NEW.PayloadJson, '$.Operation.AccountRead');
END;
CREATE TRIGGER TR_TenantApprovals_AccountRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object' AND NOT EXISTS (
SELECT 1 FROM AgentCredentialBindings b JOIN Credentials c ON c.CredentialId = b.CredentialId
JOIN TenantCredentials owned ON owned.CredentialId = c.CredentialId AND owned.TenantId = b.TenantId AND owned.DeletedAt IS NULL
WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.InstanceId AND b.CredentialId = NEW.CredentialId
AND b.BindingId = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingId')
AND b.Reference = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.Reference')
AND b.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.BindingRevision')
AND c.Revision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.CredentialRevision')
AND c.SecretRevision = json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Credential.SecretRevision')
AND c.Purpose = CASE json_extract(NEW.PayloadJson, '$.Operation.AccountRead.Purpose') WHEN 1 THEN 'EmailAccount' WHEN 2 THEN 'GoogleDriveAccount' END)
BEGIN SELECT RAISE(ABORT, 'exact account read binding required'); END;
CREATE TRIGGER TR_TenantApprovals_AccountRead_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN (json_type(OLD.PayloadJson, '$.Operation.AccountRead') IS 'object' OR json_type(NEW.PayloadJson, '$.Operation.AccountRead') IS 'object')
AND (json_remove(NEW.PayloadJson, '$.Status', '$.Revision') IS NOT json_remove(OLD.PayloadJson, '$.Status', '$.Revision')
OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.CredentialId IS NOT OLD.CredentialId
OR NEW.Kind IS NOT OLD.Kind OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt)
BEGIN SELECT RAISE(ABORT, 'account read consent is immutable'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN NEW.AutomationId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Automation consent requires its exact reserved source') WHERE NOT EXISTS (
SELECT 1 FROM TriggerOccurrences o JOIN Automations a ON a.AutomationId = o.AutomationId
WHERE o.TenantId = NEW.TenantId AND a.TenantId = NEW.TenantId AND o.AutomationId = NEW.AutomationId
AND o.AgentInstanceId = NEW.InstanceId AND a.AgentInstanceId = NEW.InstanceId
AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND o.OccurrenceId = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
AND a.Revision = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.Revision')
AND a.Name IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Name')
AND a.Instructions IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Instructions')
AND a.OccurrenceCount = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.OccurrenceCount')
AND a.Status = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Next.Status')
AND json_extract(o.EvidenceJson, '$.instructions') IS a.Instructions
AND json_extract(o.EvidenceJson, '$.automationRevision') = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Current.Revision')
AND o.ModelOfferingId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.OfferingId')
AND o.ModelConnectionId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ConnectionId')
AND o.ExternalModelId IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ExternalModelId')
AND o.ModelReasoningEffort IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Model.ReasoningEffort')
AND json_extract(o.EvidenceJson, '$.admissionApproval.requestId') IS lower(NEW.RequestId)
AND json_extract(o.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS lower(NEW.OriginalInitiatorPrincipalId)
AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
AND o.DedupeKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending' AND o.RoutingRevision = 1
AND o.ExecutionSessionId IS NULL AND o.AcceptedAgentRunId IS NULL
AND json_extract(o.EvidenceJson, '$.admissionApproval.sessionId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.SessionId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.runId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.RunId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.activationId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ActivationId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.entryId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.EntryId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.responseId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ResponseId')
);
SELECT RAISE(ABORT, 'account-source Review requires actual frozen email facts') WHERE NOT COALESCE((NEW.Permission NOT IN ('credentials.read','credentials.use','email.read') OR (NEW.Kind = 0 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS source.value AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')))),0);
END;
CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_Immutable BEFORE UPDATE ON TenantApprovalRequests
WHEN OLD.AutomationId IS NOT NULL OR NEW.AutomationId IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'Automation consent evidence is immutable') WHERE
OLD.AutomationId IS NOT NEW.AutomationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId
OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.InstanceId IS NOT NEW.InstanceId
OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId
OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceId IS NOT NEW.ResourceId
OR OLD.ResourceType IS NOT NEW.ResourceType OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.OperationId IS NOT NEW.OperationId
OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt OR OLD.Kind IS NOT NEW.Kind
OR json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationAdmission_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN NEW.AutomationId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Automation consent requires its exact reserved source') WHERE NOT EXISTS (
SELECT 1 FROM TriggerOccurrences o JOIN Automations a ON a.AutomationId = o.AutomationId
WHERE o.TenantId = NEW.TenantId AND a.TenantId = NEW.TenantId AND o.AutomationId = NEW.AutomationId
AND o.AgentInstanceId = NEW.InstanceId AND a.AgentInstanceId = NEW.InstanceId
AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND o.OccurrenceId = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.OccurrenceId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.requestId') IS lower(NEW.RequestId)
AND json_extract(o.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS lower(NEW.OriginalInitiatorPrincipalId)
AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.SourceKind')
AND o.DedupeKey IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.Occurrence.DedupeKey')
AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending' AND o.RoutingRevision = 1
AND o.ExecutionSessionId IS NULL AND o.AcceptedAgentRunId IS NULL
AND json_extract(o.EvidenceJson, '$.admissionApproval.sessionId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.SessionId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.runId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.RunId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.activationId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ActivationId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.entryId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.EntryId')
AND json_extract(o.EvidenceJson, '$.admissionApproval.responseId') IS json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission.ResponseId')
);
SELECT RAISE(ABORT, 'account-source Review requires actual frozen email facts') WHERE NOT COALESCE((NEW.Permission NOT IN ('credentials.read','credentials.use','email.read') OR (NEW.Kind = 0 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Operation.AutomationAdmission.EventSource.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS source.value AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')))),0);
END;
CREATE TRIGGER TR_TenantApprovals_AutomationInvocation_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Invocation')='object' AND NOT EXISTS
 (SELECT 1 FROM AgentRuns root WHERE root.TenantId=NEW.TenantId AND root.AgentInstanceId=NEW.InstanceId AND root.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND root.ActivationId=NEW.RootActivationId AND root.RootActivationId=root.ActivationId AND root.OriginalInitiatorPrincipalId=NEW.OriginalInitiatorPrincipalId
  AND root.SessionId=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.RootSessionId') AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0)
BEGIN SELECT RAISE(ABORT,'Manual model invocation requires its actual human request root'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationMutation_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.AutomationMutation')='object' AND
 ((json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current')='object' AND NOT EXISTS
   (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
    AND a.AutomationId=NEW.ResourceId AND a.Revision=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Current.Revision')))
 OR (json_type(NEW.PayloadJson,'$.Operation.AutomationMutation.Current') IS NOT 'object' AND EXISTS
   (SELECT 1 FROM Automations a WHERE a.AutomationId=json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation.Proposed.AutomationId'))))
BEGIN SELECT RAISE(ABORT,'Automation mutation requires exact owned current parent or fresh identity'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationMutation_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.AutomationMutation') IS NOT json_extract(OLD.PayloadJson,'$.Operation.AutomationMutation')
BEGIN SELECT RAISE(ABORT,'Retain exact Automation mutation'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationRead_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.AutomationRead') IS NOT NULL AND (EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')) source
    WHERE NOT EXISTS (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
      AND a.AutomationId=json_extract(source.value,'$.AutomationId') AND a.Revision=json_extract(source.value,'$.Revision')))
    OR (SELECT count(DISTINCT json_extract(value,'$.AutomationId')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')))<>json_array_length(json_extract(NEW.PayloadJson,'$.Operation.AutomationRead.SourcesJson')))
BEGIN SELECT RAISE(ABORT,'Automation read consent requires exact owned current parents'); END;
CREATE TRIGGER TR_TenantApprovals_AutomationRead_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.AutomationRead') IS NOT json_extract(OLD.PayloadJson,'$.Operation.AutomationRead')
BEGIN SELECT RAISE(ABORT,'Retain exact prepared Automation read'); END;
CREATE TRIGGER TR_TenantApprovals_BackgroundCreation_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
END;
CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.BackgroundReport') IS 'object'
BEGIN
SELECT RAISE(ABORT, 'Report approval must reference its exact completed child and owned parent') WHERE NOT EXISTS (
SELECT 1 FROM AgentRuns child JOIN TenantSessions origin ON origin.TenantId = child.TenantId AND origin.SessionId = child.SessionId
JOIN TenantSessions parent ON parent.TenantId = origin.TenantId AND parent.AgentInstanceId = origin.AgentInstanceId AND parent.ExecutorPrincipalId = origin.ExecutorPrincipalId
JOIN Sessions saved ON saved.SessionId = parent.SessionId
WHERE child.TenantId = NEW.TenantId AND child.AgentRunId = NEW.RunId AND child.SessionId = NEW.SessionId
AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND child.AgentInstanceId = NEW.InstanceId AND child.RootActivationId = NEW.RootActivationId
AND child.Status IN (4,5,6) AND child.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.SourceRevision')
AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentSessionId')
AND saved.Revision = json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport.ParentRevision') AND saved.DurablyDeletedAtUtc IS NULL);
END;
CREATE TRIGGER TR_TenantApprovals_BackgroundReport_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Exact background creation is immutable') WHERE
json_extract(NEW.PayloadJson, '$.Operation') IS NOT json_extract(OLD.PayloadJson, '$.Operation')
OR NEW.RequestId IS NOT OLD.RequestId OR NEW.TenantId IS NOT OLD.TenantId OR NEW.RunId IS NOT OLD.RunId
OR NEW.SessionId IS NOT OLD.SessionId OR NEW.InstanceId IS NOT OLD.InstanceId OR NEW.RootActivationId IS NOT OLD.RootActivationId
OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId
OR NEW.OperationId IS NOT OLD.OperationId OR NEW.Fingerprint IS NOT OLD.Fingerprint OR NEW.Permission IS NOT OLD.Permission
OR NEW.ResourceType IS NOT OLD.ResourceType OR NEW.ResourceId IS NOT OLD.ResourceId OR NEW.Kind IS NOT OLD.Kind
OR NEW.CreatedAt IS NOT OLD.CreatedAt OR NEW.ExpiresAt IS NOT OLD.ExpiresAt;
END;
CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT NULL
OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.screenshot'
BEGIN
SELECT RAISE(ABORT, 'Browser publication requires exact bounded intent') WHERE NOT COALESCE((
NEW.Permission IN ('browser.use','credentials.use')
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object'
AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')) = 8
AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
WHERE key IN ('Capture','Downloads','CaptureBytes','DownloadBytes','CapturesPerScope','DownloadsPerScope','CapturePrivacyMode','CapturePrivacyHash')) = 8
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') IN ('true','false')
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') IN ('true','false')
AND (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1)
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') =
(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') = 'browser.screenshot')
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') BETWEEN 1 AND 1500000
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') BETWEEN 1 AND 5242880
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') BETWEEN 1 AND 4
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') BETWEEN 1 AND 2
AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IN ('Protected','Unmasked')
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash')) = 64
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') NOT GLOB '*[^0-9a-f]*')
OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 0
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS NULL))
),0);
END;
CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT
json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
BEGIN SELECT RAISE(ABORT, 'Browser publication intent is immutable'); END;
CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop' AND
json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS NOT NULL)
OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Browser file transfer requires exact owned Artifact sources') WHERE NOT COALESCE((
NEW.Kind = 0 AND NEW.Permission = 'browser.use' AND NEW.ResourceType = 'agent' AND NEW.ResourceId = NEW.InstanceId
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IN ('browser.upload','browser.drop')
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Credential') IS NULL
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')) BETWEEN 1 AND 2048
AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') AS BLOB)) BETWEEN 1 AND 16384
AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) IS 'array'
AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) BETWEEN 1 AND 8
AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) =
json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds'))
OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop'
AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) = 1
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS 'text'
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.text') IS NULL
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.mimeType') IS NULL))
AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) source
WHERE json_type(source.value) IS NOT 'object'
OR json_type(source.value, '$.ArtifactId') IS NOT 'text'
OR json_type(source.value, '$.DisplayName') IS NOT 'text'
OR json_type(source.value, '$.ContentType') IS NOT 'text'
OR json_type(source.value, '$.ByteSize') IS NOT 'integer'
OR json_type(source.value, '$.Sha256Hex') IS NOT 'text'
OR (SELECT count(*) FROM json_each(source.value)) <> 5
OR (SELECT count(DISTINCT key) FROM json_each(source.value)
WHERE key IN ('ArtifactId','DisplayName','ContentType','ByteSize','Sha256Hex')) <> 5
OR json_extract(source.value, '$.ArtifactId') IS NOT
CASE WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
THEN json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds[' || source.key || ']')
ELSE json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') END
OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = NEW.TenantId AND a.SessionId = NEW.SessionId
AND a.ArtifactId = json_extract(source.value, '$.ArtifactId') AND a.DisplayName = json_extract(source.value, '$.DisplayName')
AND a.ContentType = json_extract(source.value, '$.ContentType') AND a.ByteSize = json_extract(source.value, '$.ByteSize')
AND a.Sha256Hex = json_extract(source.value, '$.Sha256Hex')
AND EXISTS (SELECT 1 FROM Sessions saved WHERE saved.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)))
AND (SELECT count(DISTINCT json_extract(value, '$.ArtifactId'))
FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) =
json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
AND (SELECT sum(json_extract(value, '$.ByteSize'))
FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) <= 52428800
), 0);
END;
CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT
json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')
OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT
json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')
OR ((json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL)
AND (json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName')
OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')))
BEGIN
SELECT RAISE(ABORT, 'Browser file transfer sources are immutable');
END;
CREATE TRIGGER TR_TenantApprovals_CompletionRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
BEGIN
SELECT RAISE(ABORT, 'Completion read requires one exact ordinary read Review') WHERE NOT COALESCE((
NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run'
AND NEW.ResourceId IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead') IS 'object'
AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ParentSessionId') IS NEW.SessionId
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') IS 'integer'
AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision') >= 1
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 20000
AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson')) IS 'object'
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.untrusted') = 1
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.tenantCompletionRead') = 1
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.backgroundSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.parentSessionId') IS NEW.SessionId
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.agentRunId') IS json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.sourceAgentRunId') IS NEW.ResourceId
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationJson'), '$.status') IN ('Completed','Failed','Cancelled')
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') IS 'text'
AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash')) = 64
AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') IS 'text'
AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson') AS BLOB)) <= 8192
AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'))
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) IS 'array'
AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) <= 3
AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') IS NOT NEW.SessionId
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId') IS NOT NEW.SessionId
AND json_type(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),9,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),14,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),19,1) IS '-' AND substr(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId'),24,1) IS '-' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000' AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId') IS NOT NEW.SessionId
AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL),0);
SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.InitialRunId')
JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.SessionId
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRevision')
AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
AND EXISTS (
WITH RECURSIVE lineage(id, depth, seen) AS (
SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
UNION ALL
SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
JOIN AgentRuns previous ON previous.AgentRunId = chain.id
JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
SELECT RAISE(ABORT, 'Completion metadata must match the exact source') WHERE
(SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.CompletionRead')) <> 9
OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')) file
WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 6
OR json_extract(file.value, '$.SessionId') IS NOT json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ChildSessionId')
OR NOT EXISTS (SELECT 1 FROM Artifacts a JOIN Sessions saved ON saved.SessionId = a.SessionId
WHERE a.TenantId = NEW.TenantId AND a.SessionId = json_extract(file.value, '$.SessionId') AND saved.DurablyDeletedAtUtc IS NULL
AND a.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.SourceRunId')
AND a.ArtifactId = json_extract(file.value, '$.ArtifactId') AND a.DisplayName = json_extract(file.value, '$.DisplayName')
AND a.ContentType = json_extract(file.value, '$.ContentType') AND a.ByteSize = json_extract(file.value, '$.ByteSize') AND a.Sha256Hex = json_extract(file.value, '$.Sha256Hex')))
OR (SELECT count(DISTINCT json_extract(value, '$.ArtifactId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson')))
<> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.CompletionRead.ArtifactsJson'));
END;
CREATE TRIGGER TR_TenantApprovals_CompletionRead_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Completion read evidence is immutable') WHERE
json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
END;
CREATE TRIGGER TR_TenantApprovals_ConfigurationRemedy_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy')='object'
BEGIN SELECT RAISE(ABORT,'Use the closed exact inspected configuration remedy') WHERE NOT COALESCE((NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation')) field WHERE key NOT IN ('SessionId','RootSessionId','ToolName','ArgumentsJson','ConfigurationHash','BeforeStableHash','AfterStableHash','ChangesJson','Settings','Management','AutomationChanges','UnattendedOfferingId','UnattendedReasoningEffort','Skill','Resource','Harness','Remedy','Binding')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation'))) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='settings.patch' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding') IS NULL
AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy')) field WHERE key NOT IN ('ArgumentsJson','Inspection')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy'))) AND json_valid(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson')) AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'))='object'
AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson')) field WHERE key NOT IN ('expectedInstanceRevision','option','target','goal','section','set','clear')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson')))
AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.expectedInstanceRevision')=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')
AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.section')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.section') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.set')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.clear')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.clear')
AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.option')='text' AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.option'))=64 AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.option') NOT GLOB '*[^0-9a-f]*'
AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.target')='text' AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.target')) BETWEEN 1 AND 64 AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.goal')='text' AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.goal')) BETWEEN 1 AND 200
AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection'))='object' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection')) field WHERE key NOT IN ('TenantId','ToolName','ArgumentsJson','InstanceRevision','ConfigurationHash','StableConfigurationHash','ObservationHash','SourcesJson')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection')))
AND lower(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.TenantId'))=lower(NEW.TenantId) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ToolName')='configuration.inspect'
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.InstanceRevision')=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ConfigurationHash')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ConfigurationHash') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.StableConfigurationHash')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.BeforeStableHash')
AND json_valid(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ArgumentsJson')) AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ArgumentsJson'),'$.target')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.target') AND COALESCE(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ArgumentsJson'),'$.goal'),json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.target'))=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.goal')
AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ObservationHash'))=64 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Remedy.Inspection.ObservationHash') NOT GLOB '*[^0-9a-f]*'),0); END;
CREATE TRIGGER TR_TenantApprovals_Continuity_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead') IS NOT NULL AND (
    NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.ActivationId = r.RootActivationId
        WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
            AND r.AgentInstanceId = NEW.InstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
            AND r.RootActivationId = NEW.RootActivationId AND root.TenantId = NEW.TenantId AND root.RootActivationId = root.ActivationId
            AND root.AgentInstanceId = r.AgentInstanceId AND root.ExecutorPrincipalId = r.ExecutorPrincipalId
            AND root.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.RootSessionId')
            AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
                AND lower(root.OriginalInitiatorPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId')
                AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = root.OriginalInitiatorPrincipalId))
                OR (json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0 AND json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NULL)))
    OR (SELECT count(DISTINCT json_extract(value, '$.SessionId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson'))
    OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SessionsJson')) witness WHERE
        json_type(witness.value, '$.SessionId') IS NOT 'text'
        OR json_type(witness.value, '$.Revision') IS NOT 'integer' OR json_extract(witness.value, '$.Revision') < 1
        OR json_type(witness.value, '$.ThroughEntrySequence') IS NOT 'integer' OR json_extract(witness.value, '$.ThroughEntrySequence') < 0
        OR json_type(witness.value, '$.AfterEntrySequence') IS NOT 'integer' OR json_extract(witness.value, '$.AfterEntrySequence') < 0
        OR json_type(witness.value, '$.Limit') IS NOT 'integer' OR json_extract(witness.value, '$.Limit') NOT BETWEEN 1 AND 20
        OR json_type(witness.value, '$.ObservationHash') IS NOT 'text' OR length(json_extract(witness.value, '$.ObservationHash')) <> 64
        OR json_extract(witness.value, '$.ObservationHash') GLOB '*[^0-9a-f]*'
        OR NOT EXISTS (SELECT 1 FROM TenantSessions owner JOIN Sessions saved ON saved.SessionId = owner.SessionId JOIN SessionSnapshots snapshot ON snapshot.SessionId = owner.SessionId
            WHERE owner.TenantId = NEW.TenantId AND owner.AgentInstanceId = NEW.InstanceId AND owner.ExecutorPrincipalId = NEW.ExecutorPrincipalId
                AND owner.SessionId = json_extract(witness.value, '$.SessionId') AND owner.SessionId <> NEW.SessionId AND saved.DurablyDeletedAtUtc IS NULL
                AND saved.Revision = json_extract(witness.value, '$.Revision') AND snapshot.LastEntrySequence = json_extract(witness.value, '$.ThroughEntrySequence'))
        OR NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.SourceLabelsJson')) label
            WHERE json_extract(label.value, '$.sessionId') = json_extract(witness.value, '$.SessionId')
                AND json_type(label.value, '$.title') IS 'text' AND length(json_extract(label.value, '$.title')) <= 160
                AND json_extract(label.value, '$.throughEntrySequence') = json_extract(witness.value, '$.ThroughEntrySequence')
                AND json_extract(label.value, '$.afterEntrySequence') = json_extract(witness.value, '$.AfterEntrySequence')
                AND json_extract(label.value, '$.limit') = json_extract(witness.value, '$.Limit')))
    OR (json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory') IS NOT NULL AND (
        (SELECT count(DISTINCT json_extract(value, '$.MemoryId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson'))
        OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.Memory.SourcesJson')) witness WHERE
            json_type(witness.value, '$.MemoryId') IS NOT 'text' OR json_type(witness.value, '$.Revision') IS NOT 'integer' OR json_extract(witness.value, '$.Revision') < 1
            OR json_type(witness.value, '$.ContentHash') IS NOT 'text' OR length(json_extract(witness.value, '$.ContentHash')) <> 64
            OR json_extract(witness.value, '$.ContentHash') GLOB '*[^0-9a-f]*'
            OR NOT EXISTS (SELECT 1 FROM TenantLearnedMemories fact WHERE fact.TenantId = NEW.TenantId
                AND lower(fact.MemoryId) = json_extract(witness.value, '$.MemoryId') AND fact.Revision = json_extract(witness.value, '$.Revision')
                AND fact.ContentHash = json_extract(witness.value, '$.ContentHash')
                AND ((json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NOT NULL AND lower(fact.UserPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId'))
                    OR (json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead.UserPrincipalId') IS NULL AND fact.UserPrincipalId IS NULL AND fact.Scope = 0 AND fact.ExecutorPrincipalId = NEW.ExecutorPrincipalId))
                AND ((fact.Scope = 0 AND fact.SessionId = NEW.SessionId) OR (fact.Scope = 1 AND fact.AgentInstanceId = NEW.InstanceId) OR fact.Scope = 2))))))
BEGIN SELECT RAISE(ABORT, 'Continuity approval requires exact actual-root owned historical and learned source witnesses'); END;
CREATE TRIGGER TR_TenantApprovals_Continuity_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.ContinuityRead') IS NOT json_extract(OLD.PayloadJson, '$.Operation.ContinuityRead')
BEGIN SELECT RAISE(ABORT, 'Retain the exact prepared historical continuity observation'); END;
CREATE TRIGGER TR_TenantApprovals_CredentialBinding_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding')='object'
BEGIN
SELECT RAISE(ABORT,'Use the closed safe credential binding plan') WHERE NOT COALESCE((NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation')) field WHERE key NOT IN ('SessionId','RootSessionId','ToolName','ArgumentsJson','ConfigurationHash','BeforeStableHash','AfterStableHash','ChangesJson','Settings','Management','AutomationChanges','UnattendedOfferingId','UnattendedReasoningEffort','Skill','Resource','Harness','Remedy','Binding')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation'))) AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding')) field WHERE key NOT IN ('Value','Remove','CredentialRevision','CredentialFingerprint','DisplayName','CredentialKind','CredentialPurpose','Changed','BeforeSources','AfterSources')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding')))
AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value')) field WHERE key NOT IN ('BindingId','AgentInstanceId','CredentialId','Reference','Revision','CreatedAtUtc','UpdatedAtUtc')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value')))
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Changed')='true' AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove') IN ('true','false') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.AgentInstanceId')=NEW.InstanceId
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId'))=36 AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId'))=36
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference')) BETWEEN 1 AND 64 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference') NOT GLOB '*[^a-z0-9-]*'
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision')='integer' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision')>=1 AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialRevision')='integer' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialRevision')>=1
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialFingerprint')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialFingerprint'))=64 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialFingerprint') NOT GLOB '*[^0-9a-f]*'
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.DisplayName')='text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.DisplayName')) BETWEEN 1 AND 120
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialKind') IN ('Password','ApiKey','Token','Certificate','PrivateKey','Generic') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialPurpose') IN ('General','EmailAccount','GoogleDriveAccount')
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.BeforeSources')='array' AND json_array_length(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.BeforeSources') BETWEEN 1 AND 2
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.AfterSources')='array' AND json_array_length(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.AfterSources') BETWEEN 1 AND 2
AND NOT EXISTS(SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.BeforeSources') witness WHERE NOT COALESCE(json_extract(witness.value,'$.Kind') IN ('binding','credential') AND json_extract(witness.value,'$.ResourceId')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId') AND length(json_extract(witness.value,'$.Fingerprint'))=64,0))
AND NOT EXISTS(SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.AfterSources') witness WHERE NOT COALESCE(json_extract(witness.value,'$.Kind') IN ('binding','credential') AND json_extract(witness.value,'$.ResourceId')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId') AND length(json_extract(witness.value,'$.Fingerprint'))=64,0))
AND (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=0 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='credentials.binding.propose' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson')) field WHERE key NOT IN ('expectedInstanceRevision','credentialId','reference')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson')))
 AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.credentialId')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.reference')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision')=1 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CreatedAtUtc')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.UpdatedAtUtc')
 OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=1 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='credentials.binding.remove' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson')) field WHERE key NOT IN ('expectedInstanceRevision','bindingId','expectedBindingRevision')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson')))
 AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.bindingId')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedBindingRevision')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision'))),0);
SELECT RAISE(ABORT,'Binding plan requires the actual owned credential and current association') WHERE NOT EXISTS
 (SELECT 1 FROM Credentials credential JOIN TenantCredentials owned ON owned.CredentialId=credential.CredentialId
 WHERE owned.TenantId=NEW.TenantId AND owned.DeletedAt IS NULL AND credential.CredentialId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId')
 AND credential.Revision=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialRevision') AND credential.DisplayName=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.DisplayName')
 AND credential.Kind=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialKind') AND credential.Purpose=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.CredentialPurpose')
 AND (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=0 AND credential.Status='Active' AND NOT EXISTS(SELECT 1 FROM AgentCredentialBindings choice WHERE choice.AgentInstanceId=NEW.InstanceId
  AND (choice.Reference=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference') OR choice.CredentialId=credential.CredentialId OR choice.BindingId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId')))
 OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=1 AND EXISTS(SELECT 1 FROM AgentCredentialBindings choice WHERE choice.TenantId=NEW.TenantId AND choice.AgentInstanceId=NEW.InstanceId
  AND choice.CredentialId=credential.CredentialId AND choice.BindingId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId') AND choice.Reference=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference') AND choice.Revision=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision'))));
END;
CREATE TRIGGER TR_TenantApprovals_ExperienceRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.Experience') IS NOT NULL AND (
    NOT EXISTS (SELECT 1 FROM ExperienceSettings setting WHERE setting.AgentInstanceId=NEW.InstanceId AND setting.Enabled=1 AND setting.Revision=json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.Experience.ExperienceRevision'))
    OR (SELECT count(DISTINCT json_extract(value,'$.ExperienceId')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson')))<>json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson'))
    OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.Experience.SourcesJson')) source WHERE
        NOT EXISTS (SELECT 1 FROM TenantExperiences fact WHERE fact.TenantId=NEW.TenantId AND fact.AgentInstanceId=NEW.InstanceId AND fact.ExecutorPrincipalId=NEW.ExecutorPrincipalId
            AND lower(fact.ExperienceId)=json_extract(source.value,'$.ExperienceId') AND fact.ContentHash=json_extract(source.value,'$.ContentHash') AND fact.Revision=json_extract(source.value,'$.Revision') AND fact.Visibility=0
            AND (fact.UserPrincipalId IS NULL OR lower(fact.UserPrincipalId)=json_extract(NEW.PayloadJson,'$.Operation.ContinuityRead.UserPrincipalId')))))
BEGIN SELECT RAISE(ABORT,'Experience read Review requires exact current owned fact witnesses'); END;
CREATE TRIGGER TR_TenantApprovals_Experience_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation') IS NOT NULL AND (
    NOT EXISTS (SELECT 1 FROM AgentRuns run JOIN AgentRuns root ON root.ActivationId=run.RootActivationId
        WHERE run.AgentRunId=NEW.RunId AND run.TenantId=NEW.TenantId AND run.AgentInstanceId=NEW.InstanceId AND run.ExecutorPrincipalId=NEW.ExecutorPrincipalId
            AND root.TenantId=run.TenantId AND root.AgentInstanceId=run.AgentInstanceId AND root.ExecutorPrincipalId=run.ExecutorPrincipalId AND root.RootActivationId=root.ActivationId
            AND root.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.RootSessionId')
            AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND lower(root.OriginalInitiatorPrincipalId)=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.UserPrincipalId'))
                OR (json_extract(root.PayloadJson,'$.admission.executionContext.origin')<>0 AND json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.UserPrincipalId') IS NULL)))
    OR NOT EXISTS (SELECT 1 FROM ExperienceSettings setting WHERE setting.AgentInstanceId=NEW.InstanceId AND setting.Enabled=1 AND setting.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.ExperienceRevision'))
    OR (json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.ToolName')='experience.consolidate' AND (
        NOT EXISTS (SELECT 1 FROM IdentityMaintenanceSettings setting WHERE setting.AgentInstanceId=NEW.InstanceId AND setting.AllowAgentConsolidation=1 AND setting.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.MaintenanceRevision'))
        OR (SELECT count(DISTINCT json_extract(value,'$.ExperienceId')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.SourcesJson')))<>json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.SourcesJson'))
        OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.SourcesJson')) source WHERE
            NOT EXISTS (SELECT 1 FROM TenantExperiences fact WHERE fact.TenantId=NEW.TenantId AND fact.AgentInstanceId=NEW.InstanceId AND fact.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                AND lower(fact.ExperienceId)=json_extract(source.value,'$.ExperienceId') AND fact.ContentHash=json_extract(source.value,'$.ContentHash') AND fact.Revision=json_extract(source.value,'$.Revision') AND fact.Visibility=0
                AND lower(fact.UserPrincipalId) IS json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.UserPrincipalId')))))
    OR (json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source') IS NOT NULL AND (
        NOT EXISTS (SELECT 1 FROM TenantSessions owner JOIN Sessions saved ON saved.SessionId=owner.SessionId
            WHERE owner.TenantId=NEW.TenantId AND owner.AgentInstanceId=NEW.InstanceId AND owner.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND saved.DurablyDeletedAtUtc IS NULL
                AND owner.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SessionId'))
        OR (json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.Kind')=0 AND (
            json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SourceId') IS NOT json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SessionId')
            OR json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SourceId')=NEW.SessionId
            OR NOT EXISTS (SELECT 1 FROM Sessions saved JOIN SessionSnapshots snapshot ON snapshot.SessionId=saved.SessionId
                WHERE saved.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SourceId')
                    AND saved.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SessionRevision')
                    AND snapshot.LastEntrySequence>=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.ThroughCursor'))))
        OR (json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.Kind')=1 AND NOT EXISTS (SELECT 1 FROM AgentRuns source
            WHERE source.AgentRunId=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SourceId') AND source.TenantId=NEW.TenantId AND source.AgentInstanceId=NEW.InstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                AND source.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SessionId') AND source.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.SourceRunRevision')
                AND source.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation.Source.ThroughCursor'))))))
BEGIN SELECT RAISE(ABORT,'Experience approval requires exact current actual-root sources and opt-in metadata'); END;
CREATE TRIGGER TR_TenantApprovals_Experience_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ExperienceOperation') IS NOT json_extract(OLD.PayloadJson,'$.Operation.ExperienceOperation')
BEGIN SELECT RAISE(ABORT,'Retain the exact prepared Experience operation'); END;
CREATE TRIGGER TR_TenantApprovals_HarnessCommand_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness')='object'
BEGIN
SELECT RAISE(ABORT,'Harness command requires closed exact submitted fields and assessment') WHERE NOT COALESCE((json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness') IS 'object' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness')) field WHERE key NOT IN ('ToolName','ArgumentsJson','Assessment','Changed')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness')))
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Changed')=1 AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 262144
AND json_valid(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) IS 'object' AND ((json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.knowledge.upsert' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) field WHERE key NOT IN ('expectedVersion','expectedInstanceRevision','policyRevision','expected','observed','limitation','id','content','source')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')))) OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.knowledge.remove' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) field WHERE key NOT IN ('expectedVersion','expectedInstanceRevision','policyRevision','expected','observed','limitation','id')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')))) OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.instructions.update' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) field WHERE key NOT IN ('expectedVersion','expectedInstanceRevision','policyRevision','expected','observed','limitation','content')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')))) OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.tool.select' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) field WHERE key NOT IN ('expectedVersion','expectedInstanceRevision','policyRevision','expected','observed','limitation','id','enabled')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')))) OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.tool.configure' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')) field WHERE key NOT IN ('expectedVersion','expectedInstanceRevision','policyRevision','expected','observed','limitation','id','allowUnreadUnsupportedTypes')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson')))))
AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedInstanceRevision')=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')
AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedVersion')='integer' AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.policyRevision')='integer' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedVersion')>=1 AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.policyRevision')>=1
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment') IS 'object' AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment')) field WHERE key NOT IN ('OperationId','Operation','InstanceRevision','DefinitionVersion','PolicyRevision','ApprovalId','ActionHash','Source','SavedAt','Evidence')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment')))
AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.OperationId') IS 'text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.OperationId'))=36
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Operation')=substr(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName'),9) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.InstanceRevision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedInstanceRevision')+1
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.DefinitionVersion')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedVersion') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.PolicyRevision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.policyRevision')
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.ApprovalId') IS NULL AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.ActionHash'))=64 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.ActionHash') NOT GLOB '*[^0-9a-f]*'
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Actor')='Agent' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Status')=1 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.DraftRevision')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.InstanceRevision')
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Expected')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expected') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Observed')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.observed')
AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expected')) BETWEEN 1 AND 2000 AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.observed')) BETWEEN 1 AND 2000
AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Source') IS json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.source')
AND ((json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName') IN ('harness.knowledge.upsert','harness.knowledge.remove') AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource') IS 'object' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Kind')=1)
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName') IN ('harness.instructions.update','harness.tool.select','harness.tool.configure') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='settings.patch' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource') IS NULL)) AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence')) field WHERE key NOT IN ('Actor','DraftRevision','Check','Status','Expected','Observed','Limitation')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence'))) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Check')='Instance self-management' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Limitation')=COALESCE(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.limitation'),'Agent assessment is partial; production effects and subjective quality require external evidence.') AND (
 (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.knowledge.upsert' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.upsert'
  AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.logicalPath')='knowledge/'||json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.id') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.kind')='Knowledge' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.mediaType')='text/markdown'
  AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.content')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.content') AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.source')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.source') AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.content')) BETWEEN 1 AND 32000)
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.knowledge.remove' AND
  (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.delete' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Deleted')=1
   OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.set_enabled' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.enabled')=0 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Deleted')=0 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Enabled')=0))
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.instructions.update' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.section')='instructions'
  AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set')) field WHERE key NOT IN ('agentInstructions')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set'))) AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set.agentInstructions')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.content') AND length(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.content')) BETWEEN 1 AND 8000 AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.clear')=0)
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.tool.select' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.section')='capabilities'
  AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set')) field WHERE key NOT IN ('selected')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set'))) AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.enabled') IN ('true','false')
  AND EXISTS(SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set.selected') selected WHERE selected.value=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.id'))=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.enabled') AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.clear')=0)
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ToolName')='harness.tool.configure' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.id')='attachments.read' AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.section')='capabilities'
  AND NOT EXISTS(SELECT 1 FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set')) field WHERE key NOT IN ('allowUnreadUnsupportedTypes')) AND (SELECT count(*)=count(DISTINCT key) FROM json_each(json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set'))) AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.allowUnreadUnsupportedTypes') IN ('true','false')
  AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.set.allowUnreadUnsupportedTypes')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.allowUnreadUnsupportedTypes') AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.clear')=0))),0);
SELECT RAISE(ABORT,'Harness command requires actual current version and delegated policy witnesses') WHERE NOT EXISTS
 (SELECT 1 FROM AgentInstances owner WHERE owner.InstanceId=NEW.InstanceId AND owner.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')
 AND owner.ActiveVersion=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.expectedVersion')
 AND COALESCE(json_extract(owner.HarnessManagementJson,'$.policyRevision'),1)=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Harness.ArgumentsJson'),'$.policyRevision'));
END;
CREATE TRIGGER TR_TenantApprovals_IndependentTask_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
BEGIN
SELECT RAISE(ABORT, 'independent task consent requires its exact owned independent lineage') WHERE NOT EXISTS (
WITH RECURSIVE owned AS (
SELECT run.* FROM AgentRuns run
WHERE run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.InstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.RootActivationId = NEW.RootActivationId AND run.OriginalInitiatorPrincipalId IS NULL AND run.ProfileId IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.tenantId') IS lower(NEW.TenantId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.agentPrincipalId') IS lower(NEW.ExecutorPrincipalId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.originalInitiatorPrincipalId') IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.runId') IS run.AgentRunId
AND json_extract(run.PayloadJson, '$.admission.executionContext.sessionId') IS run.SessionId
AND json_extract(run.PayloadJson, '$.admission.executionContext.activationId') IS run.ActivationId
AND json_extract(run.PayloadJson, '$.admission.executionContext.rootActivationId') IS run.RootActivationId
AND json_extract(run.PayloadJson, '$.admission.activation.rootActivationId') IS run.RootActivationId
), lineage(id, depth, seen) AS (
SELECT run.AgentRunId, 0, ',' || run.AgentRunId || ',' FROM owned run
JOIN TenantSessions session ON session.SessionId = run.SessionId
WHERE run.AgentRunId = NEW.RunId AND run.SessionId = NEW.SessionId
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND json_extract(NEW.PayloadJson, '$.OriginalInitiatorPrincipalId') IS NULL
UNION ALL
SELECT parent.AgentRunId, chain.depth + 1, chain.seen || parent.AgentRunId || ',' FROM lineage chain
JOIN owned member ON member.AgentRunId = chain.id
JOIN owned parent ON parent.AgentRunId = json_extract(member.PayloadJson, '$.admission.executionContext.parentRunId')
WHERE chain.depth < 63 AND instr(chain.seen, ',' || parent.AgentRunId || ',') = 0
AND json_extract(member.PayloadJson, '$.admission.executionContext.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceAgentRunId') IS parent.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceSessionId') IS parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.outputContract') IN (0,1)
AND json_extract(parent.PayloadJson, '$.admission.outputContract') IN (0,1)
AND ((json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 1
AND json_extract(member.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 2 AND member.SessionId <> parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.taskIntent.source') = 1
AND json_extract(member.PayloadJson, '$.admission.taskIntent.sourceRevision') IS parent.AgentRunId)
OR (json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 9
AND json_extract(member.PayloadJson, '$.admission.outputContract') IS json_extract(parent.PayloadJson, '$.admission.outputContract')
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 9 AND member.SessionId = parent.SessionId
AND parent.Status = 4 AND json_extract(parent.PayloadJson, '$.result.outcomeKind') = 3
AND json_extract(parent.PayloadJson, '$.result.continuationRunId') IS member.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.taskIntent') IS json_extract(parent.PayloadJson, '$.admission.taskIntent')
AND json_extract(member.PayloadJson, '$.admission.approvalResume.sourceRunId') IS parent.AgentRunId
AND json_type(member.PayloadJson, '$.admission.approvalResume.requestId') IS 'text'
AND json_extract(member.PayloadJson, '$.admission.approvalResume.requestId') IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND EXISTS (SELECT 1 FROM TenantApprovalRequests approval WHERE lower(approval.RequestId) IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND approval.TenantId = NEW.TenantId AND approval.RunId = parent.AgentRunId AND approval.SessionId = parent.SessionId
AND approval.RootActivationId = NEW.RootActivationId AND approval.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND approval.Status IN (1,2,3,4))))
) SELECT 1 FROM lineage chain JOIN owned root ON root.AgentRunId = chain.id
JOIN TriggerOccurrences occurrence ON occurrence.AcceptedAgentRunId = root.AgentRunId AND occurrence.ExecutionSessionId = root.SessionId
WHERE root.RootActivationId = root.ActivationId AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = CASE occurrence.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 3 THEN 6 END
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentActivationId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.source') = 0
AND json_extract(root.PayloadJson, '$.admission.taskIntent.eventSourceKind') IS CASE WHEN occurrence.SourceKind IN (1,3) THEN occurrence.SourceKind ELSE NULL END
AND json_extract(root.PayloadJson, '$.admission.activation.kind') = CASE occurrence.SourceKind WHEN 0 THEN 3 WHEN 1 THEN 4 WHEN 3 THEN 7 END
AND json_extract(root.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS lower(occurrence.OccurrenceId)
AND occurrence.TenantId = NEW.TenantId AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND ((occurrence.ExecutionTargetKind = 0 AND json_extract(root.PayloadJson, '$.admission.outputContract') = 1)
OR (occurrence.ExecutionTargetKind = 1 AND occurrence.TargetSessionId IS root.SessionId AND occurrence.ReportToSessionId IS NULL AND json_extract(root.PayloadJson, '$.admission.outputContract') = 0))
AND occurrence.SourceKind IN (0,1,3) AND occurrence.Disposition = 6 AND occurrence.ProfileId IS NULL AND occurrence.AutomationId IS NOT NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.text') IS json_extract(occurrence.EvidenceJson, '$.instructions')
AND json_extract(root.PayloadJson, '$.admission.taskIntent.sourceRevision') IS lower(occurrence.AutomationId) || ':' || json_extract(occurrence.EvidenceJson, '$.automationRevision'));
END;
CREATE TRIGGER TR_TenantApprovals_IndependentTask_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN NEW.OriginalInitiatorPrincipalId IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.AutomationAdmission') IS NULL
BEGIN
SELECT RAISE(ABORT, 'independent task consent requires its exact owned independent lineage') WHERE NOT EXISTS (
WITH RECURSIVE owned AS (
SELECT run.* FROM AgentRuns run
WHERE run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.InstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.RootActivationId = NEW.RootActivationId AND run.OriginalInitiatorPrincipalId IS NULL AND run.ProfileId IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.tenantId') IS lower(NEW.TenantId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.agentPrincipalId') IS lower(NEW.ExecutorPrincipalId)
AND json_extract(run.PayloadJson, '$.admission.executionContext.originalInitiatorPrincipalId') IS NULL
AND json_extract(run.PayloadJson, '$.admission.executionContext.runId') IS run.AgentRunId
AND json_extract(run.PayloadJson, '$.admission.executionContext.sessionId') IS run.SessionId
AND json_extract(run.PayloadJson, '$.admission.executionContext.activationId') IS run.ActivationId
AND json_extract(run.PayloadJson, '$.admission.executionContext.rootActivationId') IS run.RootActivationId
AND json_extract(run.PayloadJson, '$.admission.activation.rootActivationId') IS run.RootActivationId
), lineage(id, depth, seen) AS (
SELECT run.AgentRunId, 0, ',' || run.AgentRunId || ',' FROM owned run
JOIN TenantSessions session ON session.SessionId = run.SessionId
WHERE run.AgentRunId = NEW.RunId AND run.SessionId = NEW.SessionId
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.InstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND json_extract(NEW.PayloadJson, '$.OriginalInitiatorPrincipalId') IS NULL
UNION ALL
SELECT parent.AgentRunId, chain.depth + 1, chain.seen || parent.AgentRunId || ',' FROM lineage chain
JOIN owned member ON member.AgentRunId = chain.id
JOIN owned parent ON parent.AgentRunId = json_extract(member.PayloadJson, '$.admission.executionContext.parentRunId')
WHERE chain.depth < 63 AND instr(chain.seen, ',' || parent.AgentRunId || ',') = 0
AND json_extract(member.PayloadJson, '$.admission.executionContext.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.parentActivationId') IS parent.ActivationId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceAgentRunId') IS parent.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.activation.sourceSessionId') IS parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.outputContract') IN (0,1)
AND json_extract(parent.PayloadJson, '$.admission.outputContract') IN (0,1)
AND ((json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 1
AND json_extract(member.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 2 AND member.SessionId <> parent.SessionId
AND json_extract(member.PayloadJson, '$.admission.taskIntent.source') = 1
AND json_extract(member.PayloadJson, '$.admission.taskIntent.sourceRevision') IS parent.AgentRunId)
OR (json_extract(member.PayloadJson, '$.admission.executionContext.origin') = 9
AND json_extract(member.PayloadJson, '$.admission.outputContract') IS json_extract(parent.PayloadJson, '$.admission.outputContract')
AND json_extract(member.PayloadJson, '$.admission.activation.kind') = 9 AND member.SessionId = parent.SessionId
AND parent.Status = 4 AND json_extract(parent.PayloadJson, '$.result.outcomeKind') = 3
AND json_extract(parent.PayloadJson, '$.result.continuationRunId') IS member.AgentRunId
AND json_extract(member.PayloadJson, '$.admission.taskIntent') IS json_extract(parent.PayloadJson, '$.admission.taskIntent')
AND json_extract(member.PayloadJson, '$.admission.approvalResume.sourceRunId') IS parent.AgentRunId
AND json_type(member.PayloadJson, '$.admission.approvalResume.requestId') IS 'text'
AND json_extract(member.PayloadJson, '$.admission.approvalResume.requestId') IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND EXISTS (SELECT 1 FROM TenantApprovalRequests approval WHERE lower(approval.RequestId) IS json_extract(parent.PayloadJson, '$.approvalRequestId')
AND approval.TenantId = NEW.TenantId AND approval.RunId = parent.AgentRunId AND approval.SessionId = parent.SessionId
AND approval.RootActivationId = NEW.RootActivationId AND approval.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND approval.Status IN (1,2,3,4))))
) SELECT 1 FROM lineage chain JOIN owned root ON root.AgentRunId = chain.id
JOIN TriggerOccurrences occurrence ON occurrence.AcceptedAgentRunId = root.AgentRunId AND occurrence.ExecutionSessionId = root.SessionId
WHERE root.RootActivationId = root.ActivationId AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = CASE occurrence.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 3 THEN 6 END
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.executionContext.parentActivationId') IS NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.source') = 0
AND json_extract(root.PayloadJson, '$.admission.taskIntent.eventSourceKind') IS CASE WHEN occurrence.SourceKind IN (1,3) THEN occurrence.SourceKind ELSE NULL END
AND json_extract(root.PayloadJson, '$.admission.activation.kind') = CASE occurrence.SourceKind WHEN 0 THEN 3 WHEN 1 THEN 4 WHEN 3 THEN 7 END
AND json_extract(root.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS lower(occurrence.OccurrenceId)
AND occurrence.TenantId = NEW.TenantId AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND ((occurrence.ExecutionTargetKind = 0 AND json_extract(root.PayloadJson, '$.admission.outputContract') = 1)
OR (occurrence.ExecutionTargetKind = 1 AND occurrence.TargetSessionId IS root.SessionId AND occurrence.ReportToSessionId IS NULL AND json_extract(root.PayloadJson, '$.admission.outputContract') = 0))
AND occurrence.SourceKind IN (0,1,3) AND occurrence.Disposition = 6 AND occurrence.ProfileId IS NULL AND occurrence.AutomationId IS NOT NULL
AND json_extract(root.PayloadJson, '$.admission.taskIntent.text') IS json_extract(occurrence.EvidenceJson, '$.instructions')
AND json_extract(root.PayloadJson, '$.admission.taskIntent.sourceRevision') IS lower(occurrence.AutomationId) || ':' || json_extract(occurrence.EvidenceJson, '$.automationRevision'));
END;
CREATE TRIGGER TR_TenantApprovals_ManagementMutation_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementMutation')='object' AND (NOT EXISTS
 (SELECT 1 FROM AgentInstances owner WHERE owner.InstanceId=NEW.InstanceId
  AND owner.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')) OR NOT EXISTS
 (SELECT 1 FROM AgentRuns root WHERE root.TenantId=NEW.TenantId AND root.AgentInstanceId=NEW.InstanceId AND root.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND root.ActivationId=NEW.RootActivationId AND root.RootActivationId=root.ActivationId AND root.OriginalInitiatorPrincipalId=NEW.OriginalInitiatorPrincipalId
  AND root.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.RootSessionId') AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0))
BEGIN SELECT RAISE(ABORT,'Management mutation requires its exact current Instance and human request root'); END;
CREATE TRIGGER TR_TenantApprovals_ManagementMutation_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation') IS NOT json_extract(OLD.PayloadJson,'$.Operation.ManagementMutation')
BEGIN SELECT RAISE(ABORT,'Retain exact prepared management mutation'); END;
CREATE TRIGGER TR_TenantApprovals_ManagementRead_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementRead')='object' AND (NOT EXISTS
 (SELECT 1 FROM AgentInstances owner WHERE owner.InstanceId=NEW.InstanceId
  AND owner.Revision=json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.InstanceRevision')) OR NOT EXISTS
 (SELECT 1 FROM AgentRuns root WHERE root.TenantId=NEW.TenantId AND root.AgentInstanceId=NEW.InstanceId AND root.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND root.ActivationId=NEW.RootActivationId AND root.RootActivationId=root.ActivationId AND root.OriginalInitiatorPrincipalId IS NEW.OriginalInitiatorPrincipalId
  AND root.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.RootSessionId') AND (json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 OR json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ToolName')='skills.load' AND json_extract(root.PayloadJson,'$.admission.executionContext.origin') IN (4,5,6,7))))
BEGIN SELECT RAISE(ABORT,'Management read requires its exact current Instance and human request root'); END;
CREATE TRIGGER TR_TenantApprovals_ManagementRead_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ManagementRead') IS NOT json_extract(OLD.PayloadJson,'$.Operation.ManagementRead')
BEGIN SELECT RAISE(ABORT,'Retain exact prepared management read'); END;
CREATE TRIGGER TR_TenantApprovals_ManagementResource_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource')='object'
BEGIN
 SELECT RAISE(ABORT,'Resource plan requires exact owned mutations and factual source witnesses') WHERE NOT COALESCE((
 NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource') member WHERE member.key NOT IN ('TenantId','TargetKey','CreateScope','SnapshotHash','PreparedAt','AllocatedId','CopiedSource','DefinitionState','InstanceResource','DeleteResourceId','ExpectedResourceRevision','ExpectedStateRevision','Changed','BeforeSources','AfterSources','Result','Source','Before')) AND json_array_length(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.BeforeSources')=(SELECT count(DISTINCT json_extract(source.value,'$.Key')) FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.BeforeSources') source)
 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.BeforeSources') source
 WHERE (SELECT count(*) FROM json_each(source.value))<>4 OR json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') IS NOT 'resource'
 OR json_type(source.value,'$.Key') IS NOT 'text' OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
 OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64 OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*') AND json_array_length(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.AfterSources')=(SELECT count(DISTINCT json_extract(source.value,'$.Key')) FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.AfterSources') source)
 AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.AfterSources') source
 WHERE (SELECT count(*) FROM json_each(source.value))<>4 OR json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') IS NOT 'resource'
 OR json_type(source.value,'$.Key') IS NOT 'text' OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
 OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64 OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*')
 AND (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')<>'resources.upsert' OR (
 NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source') member WHERE member.key NOT IN ('Name','RootRunId','MaterialHash','ToolCallId','ToolName','ArgumentsJson','ReadRequestId','ReadOperationId','Workspace','Attachment'))
 AND EXISTS (SELECT 1 FROM AgentRuns root WHERE root.AgentRunId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.RootRunId') AND root.TenantId=NEW.TenantId
  AND root.AgentInstanceId=NEW.InstanceId AND root.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND root.ActivationId=NEW.RootActivationId AND root.RootActivationId=root.ActivationId
  AND root.SessionId=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.RootSessionId') AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0)
 AND ((json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Name')='conversation:user' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolCallId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolName') IS NULL
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadRequestId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadOperationId') IS NULL
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment') IS NULL)
 OR (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Name')<>'conversation:user' AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolCallId') IS 'text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolCallId')) BETWEEN 1 AND 200
  AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolName') IS 'text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolName')) BETWEEN 1 AND 100
  AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson') AS BLOB)) BETWEEN 2 AND 262144
  AND json_valid(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson')) AND json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson'))='object'
  AND ((json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace') IS 'object')+(json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment') IS 'object'))<=1
  AND (json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace') IS 'object' OR json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment') IS 'object' OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolName') GLOB 'browser.*')
  AND ((json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadRequestId') IS NULL AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadOperationId') IS NULL)
   OR (json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadRequestId') IS 'text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadRequestId'))=36
    AND json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadOperationId') IS 'text' AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadOperationId'))=36
    AND EXISTS (SELECT 1 FROM TenantApprovalRequests source JOIN TenantOperationReceipts receipt ON receipt.RequestId=source.RequestId
     WHERE lower(source.RequestId)=lower(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadRequestId')) AND lower(source.OperationId)=lower(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ReadOperationId'))
     AND source.TenantId=NEW.TenantId AND source.InstanceId=NEW.InstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId
     AND source.SessionId=NEW.SessionId AND source.RootActivationId=NEW.RootActivationId AND source.Status=1
     AND receipt.TenantId=source.TenantId AND receipt.InstanceId=source.InstanceId AND receipt.ExecutorPrincipalId=source.ExecutorPrincipalId
     AND receipt.OperationId=source.OperationId AND receipt.ActionHash=source.Fingerprint AND receipt.Outcome=0
     AND (json_extract(source.PayloadJson,'$.Operation.WorkspaceRead')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Workspace')
       OR json_extract(source.PayloadJson,'$.Operation.AttachmentRead')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.Attachment')
       OR (json_extract(source.PayloadJson,'$.Operation.BrowserCommand.ToolName')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ToolName') AND json_extract(source.PayloadJson,'$.Operation.BrowserCommand.ArgumentsJson')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Source.ArgumentsJson')))))))))) AND ((json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource') IS 'object' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.InstanceId') IS NEW.InstanceId
 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Key') IS ('instance:'||json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.ResourceId')) AND length(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.ResourceId'))=36
 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Revision') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.Revision') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.ContentSha256') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.ContentSha256')
 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.ByteLength') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.ByteLength') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.LogicalPath') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.LogicalPath')
 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.MediaType') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.MediaType') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Kind') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.Kind')
 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Enabled') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.Enabled')
 AND (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.CreateScope')=1 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.Revision')=1 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedResourceRevision') IS NULL
  OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.CreateScope')=0 AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedResourceRevision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedResourceRevision')
   AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.InstanceResource.Revision')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedResourceRevision')+1))
 OR (json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState') IS 'object' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.set_enabled' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState.InstanceId') IS NEW.InstanceId
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Key') IS ('definition:'||json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState.ResourceId')) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.TargetKey') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Key')
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Revision') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState.Revision') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.EnabledOverride') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState.EnabledOverride')
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DefinitionState.Revision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedResourceRevision')+1
  AND (json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedStateRevision') IS NULL AND json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedResourceRevision')=0
   OR json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedStateRevision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedResourceRevision')))
 OR (json_type(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DeleteResourceId') IS 'text' AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ToolName')='resources.delete'
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Key') IS ('instance:'||json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.DeleteResourceId')) AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.TargetKey') IS json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Key')
  AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedResourceRevision')=json_extract(json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.ArgumentsJson'),'$.expectedResourceRevision') AND json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.Result.Revision')=json_extract(NEW.PayloadJson,'$.Operation.ManagementMutation.Resource.ExpectedResourceRevision')))
 ),0);
END;
CREATE TRIGGER TR_TenantApprovals_Memory_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT NULL AND (
    NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.ActivationId = r.RootActivationId
        WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
            AND r.AgentInstanceId = NEW.InstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
            AND root.TenantId = NEW.TenantId AND root.RootActivationId = root.ActivationId
            AND root.AgentInstanceId = r.AgentInstanceId AND root.ExecutorPrincipalId = r.ExecutorPrincipalId
            AND root.SessionId = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.RootSessionId') AND r.RootActivationId = NEW.RootActivationId
            AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
                AND lower(root.OriginalInitiatorPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId')
                AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = root.OriginalInitiatorPrincipalId))
                OR (json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0
                    AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') IN ('continuity.search','continuity.get'))))
    OR EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source WHERE NOT EXISTS (
        SELECT 1 FROM TenantLearnedMemories m WHERE m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')
            AND m.Revision = json_extract(source.value, '$.Revision') AND m.ContentHash = json_extract(source.value, '$.ContentHash')
            AND ((json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NOT NULL AND lower(m.UserPrincipalId) = json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId'))
                OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.UserPrincipalId') IS NULL AND m.UserPrincipalId IS NULL AND m.Scope = 0 AND m.ExecutorPrincipalId = NEW.ExecutorPrincipalId))
            AND ((m.Scope = 0 AND m.SessionId = NEW.SessionId) OR (m.Scope = 1 AND m.AgentInstanceId = NEW.InstanceId) OR m.Scope = 2)
            AND (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') NOT IN ('memory.consolidate','memory.forget') OR
                (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND m.Status = 0) OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.forget' AND m.Status <> 2))
            AND EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourceLabelsJson')) label
                WHERE json_extract(label.value, '$.memoryId') = lower(m.MemoryId)
                    AND json_extract(label.value, '$.subject') = m.Subject
                    AND json_extract(label.value, '$.scope') = CASE m.Scope WHEN 0 THEN 'session' WHEN 1 THEN 'identityUser' ELSE 'user' END
                    AND json_extract(label.value, '$.kind') = CASE m.Kind WHEN 0 THEN 'Fact' WHEN 1 THEN 'Preference' WHEN 2 THEN 'Goal' WHEN 3 THEN 'Decision' ELSE 'OpenLoop' END)))
    OR (SELECT count(DISTINCT json_extract(value, '$.MemoryId')) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))) <> json_array_length(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson'))
    OR (json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.ToolName') = 'memory.consolidate' AND (SELECT count(DISTINCT m.OwnerKey || ':' || m.Kind) FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation.SourcesJson')) source
        JOIN TenantLearnedMemories m ON m.TenantId = NEW.TenantId AND lower(m.MemoryId) = json_extract(source.value, '$.MemoryId')) <> 1)
)
BEGIN SELECT RAISE(ABORT, 'Memory approval requires exact actual-root owned source witnesses'); END;
CREATE TRIGGER TR_TenantApprovals_Memory_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson, '$.Operation.MemoryOperation') IS NOT json_extract(OLD.PayloadJson, '$.Operation.MemoryOperation')
BEGIN SELECT RAISE(ABORT, 'Retain exact prepared Memory operation'); END;
CREATE TRIGGER TR_TenantApprovals_PrivateSkill_INSERT BEFORE INSERT ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ToolName')='skills.load' AND NOT COALESCE((
 json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')='array'
 AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids') BETWEEN 1 AND 4
 AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson')))=1
 AND (SELECT count(DISTINCT value) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids'))=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids') selected
   WHERE selected.type<>'text' OR NOT EXISTS (SELECT 1 FROM AgentRuns run, json_each(run.PayloadJson,'$.pinnedSkillCatalog') pin
     WHERE run.AgentRunId=NEW.RunId AND run.TenantId=NEW.TenantId AND run.AgentInstanceId=NEW.InstanceId AND run.ExecutorPrincipalId=NEW.ExecutorPrincipalId
       AND json_extract(pin.value,'$.key')=selected.value AND json_extract(pin.value,'$.origin') IN (0,1)))
 AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson'))=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND (SELECT count(DISTINCT json_extract(source.value,'$.Key')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson')) source)=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson')) source
   WHERE source.type<>'object' OR (SELECT count(*) FROM json_each(source.value))<>4
    OR json_extract(source.value,'$.Kind') IS NOT 'skillProcedure' OR json_type(source.value,'$.Key') IS NOT 'text'
    OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64 OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*'
    OR json_extract(source.value,'$.ResourceId') IS NOT NEW.InstanceId||':'||json_extract(source.value,'$.Key')
    OR json_extract(source.value,'$.Key') NOT IN (SELECT value FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')))
),0)
BEGIN SELECT RAISE(ABORT,'Private Skill consent requires exact owned pinned procedures'); END;
CREATE TRIGGER TR_TenantApprovals_PrivateSkill_UPDATE BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ToolName')='skills.load' AND NOT COALESCE((
 json_type(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')='array'
 AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids') BETWEEN 1 AND 4
 AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson')))=1
 AND (SELECT count(DISTINCT value) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids'))=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids') selected
   WHERE selected.type<>'text' OR NOT EXISTS (SELECT 1 FROM AgentRuns run, json_each(run.PayloadJson,'$.pinnedSkillCatalog') pin
     WHERE run.AgentRunId=NEW.RunId AND run.TenantId=NEW.TenantId AND run.AgentInstanceId=NEW.InstanceId AND run.ExecutorPrincipalId=NEW.ExecutorPrincipalId
       AND json_extract(pin.value,'$.key')=selected.value AND json_extract(pin.value,'$.origin') IN (0,1)))
 AND json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson'))=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND (SELECT count(DISTINCT json_extract(source.value,'$.Key')) FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson')) source)=json_array_length(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')
 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.SourcesJson')) source
   WHERE source.type<>'object' OR (SELECT count(*) FROM json_each(source.value))<>4
    OR json_extract(source.value,'$.Kind') IS NOT 'skillProcedure' OR json_type(source.value,'$.Key') IS NOT 'text'
    OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64 OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*'
    OR json_extract(source.value,'$.ResourceId') IS NOT NEW.InstanceId||':'||json_extract(source.value,'$.Key')
    OR json_extract(source.value,'$.Key') NOT IN (SELECT value FROM json_each(json_extract(NEW.PayloadJson,'$.Operation.ManagementRead.ArgumentsJson'),'$.ids')))
),0)
BEGIN SELECT RAISE(ABORT,'Private Skill consent requires exact owned pinned procedures'); END;
CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Insert BEFORE INSERT ON TenantApprovalRequests
WHEN json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
BEGIN
SELECT RAISE(ABORT, 'Report source read requires exact ordinary Review') WHERE NOT COALESCE((
NEW.Kind = 0 AND NEW.Status IN (0,1,2,3,4) AND NEW.Revision >= 1 AND NEW.ExpiresAt > NEW.CreatedAt AND length(NEW.Fingerprint) = 64
AND NEW.Permission = 'agent-runs.read' AND NEW.ResourceType = 'agent-run' AND NEW.ResourceId IS NEW.RunId
AND NEW.CredentialId IS NULL AND NEW.SandboxProfileId IS NULL AND NEW.SandboxProfileRevision IS NULL
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS 'object'
AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NEW.SessionId
AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NEW.RunId
AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT NEW.SessionId
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') IS 'integer' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision') >= 1
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.Objective') AS BLOB)) BETWEEN 1 AND 4000
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') IS 'text' AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson') AS BLOB)) BETWEEN 2 AND 8192
AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'))
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.TriggerKind') = 7
AND json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.InputBoundarySequence') IS NULL
AND json_type(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text') IS 'text'
AND json_valid(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'))
AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.childSessionId') IS NEW.SessionId
AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.initialAgentRunId') IS NEW.RunId
AND json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.status') IN ('Completed','Failed','Cancelled')
AND json_array_length(json_extract(json_extract(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationJson'), '$.Text'), '$.artifacts')) = 0
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash')) = 64
AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ObservationHash') NOT GLOB '*[^0-9a-f]*'
AND length(CAST(NEW.PayloadJson AS BLOB)) <= 131072
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_type(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS 'text' AND length(json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')) = 36 AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId') IS NOT '00000000-0000-0000-0000-000000000000'
AND json_extract(NEW.PayloadJson, '$.Operation.Persona') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.Preferences') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.CredentialCheck') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxCommand') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxControl') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.SandboxRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.CredentialRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactPublication') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.ArtifactVerification') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.WorkspaceMutation') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.AttachmentRead') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundStart') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.BackgroundReport') IS NULL
AND json_extract(NEW.PayloadJson, '$.Operation.CompletionRead') IS NULL),0);
SELECT RAISE(ABORT, 'Completion read source must be an exact owned terminal child') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions child JOIN Sessions saved ON saved.SessionId = child.SessionId
JOIN AgentRuns initial ON initial.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.InitialRunId')
JOIN AgentRuns source ON source.AgentRunId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRunId')
WHERE child.TenantId = NEW.TenantId AND child.AgentInstanceId = NEW.InstanceId AND child.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND child.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ChildSessionId') AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS initial.AgentRunId
AND initial.TenantId = NEW.TenantId AND initial.SessionId = child.SessionId AND initial.AgentInstanceId = NEW.InstanceId AND initial.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND (json_extract(initial.PayloadJson, '$.admission.activation.kind') = 2 OR EXISTS (
SELECT 1 FROM TriggerOccurrences occurrence WHERE occurrence.TenantId = NEW.TenantId
AND occurrence.AgentInstanceId = NEW.InstanceId AND occurrence.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND occurrence.AcceptedAgentRunId = initial.AgentRunId AND occurrence.ExecutionSessionId = initial.SessionId
AND occurrence.Disposition = 6 AND occurrence.ExecutionTargetKind = 0 AND occurrence.TargetSessionId IS NULL
AND occurrence.ReportToSessionId IS json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId')
AND occurrence.OccurrenceId IS json_extract(initial.PayloadJson, '$.admission.activation.triggerOccurrenceId')
AND occurrence.AutomationId IS json_extract(saved.OriginJson, '$.automationId')
AND occurrence.OccurrenceId IS json_extract(saved.OriginJson, '$.triggerOccurrenceId')
AND initial.RootActivationId = initial.ActivationId AND json_extract(initial.PayloadJson, '$.admission.executionContext.parentRunId') IS NULL)) AND json_extract(initial.PayloadJson, '$.admission.outputContract') = 1
AND source.TenantId = NEW.TenantId AND source.SessionId = child.SessionId AND source.AgentInstanceId = NEW.InstanceId AND source.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND source.AgentRunId IS NEW.RunId AND source.SessionId IS NEW.SessionId AND source.RootActivationId IS NEW.RootActivationId
AND source.RootActivationId = initial.RootActivationId AND source.Status IN (4,5,6)
AND source.Revision = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.SourceRevision')
AND json_extract(source.PayloadJson, '$.admission.outputContract') = 1
AND (json_extract(source.PayloadJson, '$.result.outcomeKind') IS NULL OR json_extract(source.PayloadJson, '$.result.outcomeKind') <> 3)
AND EXISTS (SELECT 1 FROM TenantSessions parent JOIN Sessions ps ON ps.SessionId = parent.SessionId WHERE parent.TenantId = NEW.TenantId
AND parent.AgentInstanceId = NEW.InstanceId AND parent.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND ps.DurablyDeletedAtUtc IS NULL
AND parent.SessionId = json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead.ParentSessionId'))
AND EXISTS (
WITH RECURSIVE lineage(id, depth, seen) AS (
SELECT initial.AgentRunId, 0, ',' || initial.AgentRunId || ','
UNION ALL
SELECT next.AgentRunId, chain.depth + 1, chain.seen || next.AgentRunId || ',' FROM lineage chain
JOIN AgentRuns previous ON previous.AgentRunId = chain.id
JOIN AgentRuns next ON next.AgentRunId = json_extract(previous.PayloadJson, '$.result.continuationRunId')
WHERE chain.depth < 63 AND previous.Status = 4 AND json_extract(previous.PayloadJson, '$.result.outcomeKind') = 3
AND instr(chain.seen, ',' || next.AgentRunId || ',') = 0
AND next.TenantId = NEW.TenantId AND next.SessionId = child.SessionId AND next.AgentInstanceId = NEW.InstanceId AND next.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND next.RootActivationId = initial.RootActivationId AND json_extract(next.PayloadJson, '$.admission.activation.kind') = 9
AND json_extract(next.PayloadJson, '$.admission.outputContract') = 1
AND json_extract(next.PayloadJson, '$.admission.approvalResume.sourceRunId') IS previous.AgentRunId
AND json_extract(next.PayloadJson, '$.admission.executionContext.parentRunId') IS previous.AgentRunId
AND json_extract(next.PayloadJson, '$.admission.activation.sourceAgentRunId') IS previous.AgentRunId
) SELECT 1 FROM lineage WHERE id = source.AgentRunId));
SELECT RAISE(ABORT, 'Report source read has an invalid shape') WHERE (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.ReportSourceRead')) <> 8;
END;
CREATE TRIGGER TR_TenantApprovals_ReportSourceRead_Update BEFORE UPDATE ON TenantApprovalRequests
WHEN json_extract(OLD.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL OR json_extract(NEW.PayloadJson, '$.Operation.ReportSourceRead') IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Report source read evidence is immutable') WHERE
json_extract(OLD.PayloadJson, '$.Operation') IS NOT json_extract(NEW.PayloadJson, '$.Operation')
OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId
OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
OR OLD.OriginalInitiatorPrincipalId IS NOT NEW.OriginalInitiatorPrincipalId OR OLD.OperationId IS NOT NEW.OperationId
OR OLD.Fingerprint IS NOT NEW.Fingerprint OR OLD.Permission IS NOT NEW.Permission OR OLD.ResourceType IS NOT NEW.ResourceType
OR OLD.ResourceId IS NOT NEW.ResourceId OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAt IS NOT NEW.CreatedAt OR OLD.ExpiresAt IS NOT NEW.ExpiresAt;
END;
CREATE TRIGGER TR_TenantAutomations_Owner_Update BEFORE UPDATE ON Automations
WHEN (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL)
AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AutomationId IS NOT NEW.AutomationId)
BEGIN SELECT RAISE(ABORT, 'tenant Automation owner is immutable'); END;
CREATE TRIGGER TR_TenantCompletion_Automation_Insert BEFORE INSERT ON Automations
WHEN NEW.TenantId IS NOT NULL AND NEW.ReportToSessionId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant completion destination must be an owned conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId = NEW.ReportToSessionId AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND NEW.ExecutionTargetKind = 0 AND NEW.TargetSessionId IS NULL);
END;
CREATE TRIGGER TR_TenantCompletion_Automation_Update BEFORE UPDATE ON Automations
WHEN NEW.TenantId IS NOT NULL AND NEW.ReportToSessionId IS NOT NULL AND (OLD.ReportToSessionId IS NOT NEW.ReportToSessionId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId)
BEGIN
SELECT RAISE(ABORT, 'tenant completion destination must be an owned conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId = NEW.ReportToSessionId AND saved.DurablyDeletedAtUtc IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND NEW.ExecutionTargetKind = 0 AND NEW.TargetSessionId IS NULL);
END;
CREATE TRIGGER TR_TenantCompletion_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence destination differs from exact admission') WHERE NOT EXISTS (
SELECT 1 FROM Automations automation WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND automation.ExecutionTargetKind IS NEW.ExecutionTargetKind AND automation.TargetSessionId IS NEW.TargetSessionId
AND automation.ReportToSessionId IS NEW.ReportToSessionId)
AND (NEW.Disposition = 6 AND 1=1);
SELECT RAISE(ABORT, 'tenant completion origin differs from accepted occurrence') WHERE NEW.Disposition = 6 AND NOT EXISTS (
SELECT 1 FROM Sessions saved WHERE saved.SessionId = NEW.ExecutionSessionId
AND ((NEW.ExecutionTargetKind = 1 AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0) OR (NEW.ExecutionTargetKind = 0 AND json_extract(saved.OriginJson, '$.kind') = 2 AND json_extract(saved.OriginJson, '$.automationId') IS NEW.AutomationId
AND json_extract(saved.OriginJson, '$.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS NEW.AcceptedAgentRunId
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.ReportToSessionId
AND json_extract(saved.OriginJson, '$.reportCompletionToOrigin') IS CASE WHEN NEW.ReportToSessionId IS NULL THEN 0 ELSE 1 END)));
END;
CREATE TRIGGER TR_TenantCompletion_Occurrence_Update BEFORE UPDATE ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence destination differs from exact admission') WHERE NOT EXISTS (
SELECT 1 FROM Automations automation WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND automation.ExecutionTargetKind IS NEW.ExecutionTargetKind AND automation.TargetSessionId IS NEW.TargetSessionId
AND automation.ReportToSessionId IS NEW.ReportToSessionId)
AND (NEW.Disposition = 6 AND OLD.Disposition <> 6);
SELECT RAISE(ABORT, 'tenant completion origin differs from accepted occurrence') WHERE NEW.Disposition = 6 AND NOT EXISTS (
SELECT 1 FROM Sessions saved WHERE saved.SessionId = NEW.ExecutionSessionId
AND ((NEW.ExecutionTargetKind = 1 AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0) OR (NEW.ExecutionTargetKind = 0 AND json_extract(saved.OriginJson, '$.kind') = 2 AND json_extract(saved.OriginJson, '$.automationId') IS NEW.AutomationId
AND json_extract(saved.OriginJson, '$.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(saved.OriginJson, '$.initialBackgroundAgentRunId') IS NEW.AcceptedAgentRunId
AND json_extract(saved.OriginJson, '$.originatingSessionId') IS NEW.ReportToSessionId
AND json_extract(saved.OriginJson, '$.reportCompletionToOrigin') IS CASE WHEN NEW.ReportToSessionId IS NULL THEN 0 ELSE 1 END)));
END;
CREATE TRIGGER TR_TenantConversation_Automation_Insert BEFORE INSERT ON Automations WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1
BEGIN
SELECT RAISE(ABORT, 'tenant execution target requires its owned UserChat conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId WHERE target.TenantId = NEW.TenantId
AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND target.SessionId IS NEW.TargetSessionId
AND NEW.ReportToSessionId IS NULL AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0);
END;
CREATE TRIGGER TR_TenantConversation_Automation_Update BEFORE UPDATE ON Automations WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND (OLD.TargetSessionId IS NOT NEW.TargetSessionId OR OLD.ExecutionTargetKind IS NOT NEW.ExecutionTargetKind OR OLD.TenantId IS NOT NEW.TenantId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId)
BEGIN
SELECT RAISE(ABORT, 'tenant execution target requires its owned UserChat conversation') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId WHERE target.TenantId = NEW.TenantId
AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND target.SessionId IS NEW.TargetSessionId
AND NEW.ReportToSessionId IS NULL AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0);
END;
CREATE TRIGGER TR_TenantConversation_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND NEW.Disposition = 6
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires an exact independent conversation Run') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId IS NEW.TargetSessionId AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND saved.DurablyDeletedAtUtc IS NULL
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId IS target.SessionId AND run.RootActivationId IS run.ActivationId AND json_extract(run.PayloadJson, '$.admission.outputContract') = 0
AND json_extract(run.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(run.PayloadJson, '$.admission.taskIntent.source') = 0 AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.sourceRevision') IS NEW.AutomationId || ':' || json_extract(NEW.EvidenceJson, '$.automationRevision'));
END;
CREATE TRIGGER TR_TenantConversation_Occurrence_Update BEFORE UPDATE ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.ExecutionTargetKind = 1 AND NEW.Disposition = 6
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires an exact independent conversation Run') WHERE NOT EXISTS (
SELECT 1 FROM TenantSessions target JOIN Sessions saved ON saved.SessionId = target.SessionId JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
WHERE target.TenantId = NEW.TenantId AND target.AgentInstanceId = NEW.AgentInstanceId AND target.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND target.SessionId IS NEW.TargetSessionId AND NEW.TargetSessionId IS NEW.ExecutionSessionId AND NEW.ReportToSessionId IS NULL
AND json_extract(saved.OriginJson, '$.kind') = 0 AND saved.DurablyDeletedAtUtc IS NULL
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId IS target.SessionId AND run.RootActivationId IS run.ActivationId AND json_extract(run.PayloadJson, '$.admission.outputContract') = 0
AND json_extract(run.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS NEW.OccurrenceId
AND json_extract(run.PayloadJson, '$.admission.taskIntent.source') = 0 AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.sourceRevision') IS NEW.AutomationId || ':' || json_extract(NEW.EvidenceJson, '$.automationRevision'));
END;
CREATE TRIGGER TR_TenantCore_Delivery_Delete BEFORE DELETE ON CoreEventDeliveries
WHEN OLD.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant event facts and coverage are retained');
END;
CREATE TRIGGER TR_TenantCore_Delivery_Insert BEFORE INSERT ON CoreEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 9 AND (NEW.Status NOT IN (1,6,8,9) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (8,9) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 3 AND ((NEW.Status = 9 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 8 AND o.Disposition = 6) OR (NEW.Status = 4 AND o.Disposition = 4)))))),0);
END;
CREATE TRIGGER TR_TenantCore_Delivery_Update BEFORE UPDATE ON CoreEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'tenant delivery source is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.EventId IS NOT NEW.EventId OR OLD.SnapshotJson IS NOT NEW.SnapshotJson);
SELECT RAISE(ABORT, 'terminal delivery cannot be reopened') WHERE OLD.TenantId IS NOT NULL AND (OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Status IN (2,4,5,6,7,8) AND (OLD.Status IS NOT NEW.Status OR OLD.DecisionJson IS NOT NEW.DecisionJson OR OLD.Code IS NOT NEW.Code));
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 9 AND (NEW.Status NOT IN (1,6,8,9) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (8,9) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 3 AND ((NEW.Status = 9 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 8 AND o.Disposition = 6) OR (NEW.Status = 4 AND o.Disposition = 4)))))),0);
END;
CREATE TRIGGER TR_TenantCore_Fact_Delete BEFORE DELETE ON CoreEvents
WHEN OLD.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant event facts and coverage are retained');
END;
CREATE TRIGGER TR_TenantCore_Fact_Insert BEFORE INSERT ON CoreEvents
BEGIN
SELECT RAISE(ABORT, 'Core facts require a closed actual source and frozen subscriptions') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.SubscriptionsJson IS NULL AND NEW.EmailWatchEventId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.PayloadJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$')) = 9 AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$') WHERE key IN ('EventId','DedupeKey','Owner','Key','OccurredAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 9) AND (json_type(NEW.PayloadJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.PayloadJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.PayloadJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.PayloadJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.PayloadJson, '$.Owner.profileId') IS 'null' AND NEW.Snapshotted = 1 AND json_type(NEW.PayloadJson, '$.TriggerDepth') IS 'integer' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') BETWEEN 0 AND 4 AND json_type(NEW.PayloadJson, '$.VisitedAutomationIds') IS 'array' AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') <= 4 AND (SELECT count(DISTINCT value) FROM json_each(NEW.PayloadJson, '$.VisitedAutomationIds')) = json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.VisitedAutomationIds') v WHERE NOT EXISTS (SELECT 1 FROM Automations a WHERE a.TenantId = NEW.TenantId AND a.AgentInstanceId = NEW.AgentInstanceId AND lower(a.ExecutorPrincipalId) = lower(NEW.ExecutorPrincipalId) AND a.AutomationId = v.value)) AND (json_type(NEW.PayloadJson, '$.RootAgentRunId') IS 'null' OR EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId AND r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND r.AgentRunId = json_extract(NEW.PayloadJson, '$.RootAgentRunId'))) AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.OccurredAtUtc')) - 2440587.5) * 86400000) AS INTEGER) = NEW.ReceivedAtUtc AND ((json_extract(NEW.PayloadJson, '$.Key') IN ('run.completed','run.failed') AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 5 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentRunId','sessionId','activationKind','outcomeKind','failureCode')) = 5) AND EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId AND r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND r.AgentRunId = json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.agentRunId') AND r.SessionId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.sessionId') AND r.Status = CASE json_extract(NEW.PayloadJson, '$.Key') WHEN 'run.completed' THEN 4 ELSE 5 END AND r.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || r.AgentRunId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.activationKind') IS CASE json_extract(r.PayloadJson, '$.admission.activation.kind') WHEN 0 THEN 'UserTurn' WHEN 1 THEN 'Initiative' WHEN 2 THEN 'ImmediateBackground' WHEN 3 THEN 'ScheduledWork' WHEN 4 THEN 'ApplicationEvent' WHEN 5 THEN 'ManualBackground' WHEN 6 THEN 'BackgroundCompleted' WHEN 7 THEN 'CoreEvent' WHEN 8 THEN 'ConfigurationContinuation' WHEN 9 THEN 'ApprovalContinuation' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.outcomeKind') IS CASE json_extract(r.PayloadJson, '$.result.outcomeKind') WHEN 0 THEN 'Response' WHEN 1 THEN 'NoAction' WHEN 2 THEN 'NeedsAttention' WHEN 3 THEN 'Continued' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.failureCode') IS json_extract(r.PayloadJson, '$.failure.code'))) OR (json_extract(NEW.PayloadJson, '$.Key') IN ('session.completed','session.ended') AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 3 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('sessionId','previousLifecycle','lifecycle')) = 3) AND EXISTS (SELECT 1 FROM TenantSessions own JOIN Sessions s ON s.SessionId = own.SessionId JOIN SessionSnapshots ss ON ss.SessionId = s.SessionId WHERE own.TenantId = NEW.TenantId AND own.AgentInstanceId = NEW.AgentInstanceId AND own.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND s.SessionId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.sessionId') AND ss.LifecycleStatus IS CASE json_extract(NEW.PayloadJson, '$.Key') WHEN 'session.completed' THEN 'Completed' ELSE 'Ended' END AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.lifecycle') IS ss.LifecycleStatus AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousLifecycle') IN ('Active','Paused','Completed','Expired','Cancelled','Ended') AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousLifecycle') IS NOT ss.LifecycleStatus AND s.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || s.SessionId || ':' || s.Revision)) OR (EXISTS (SELECT 1 FROM AgentInstances i WHERE i.InstanceId = NEW.AgentInstanceId AND i.UpdatedAtUtc = NEW.ReceivedAtUtc AND NEW.DedupeKey IS json_extract(NEW.PayloadJson, '$.Key') || ':' || i.InstanceId || ':' || i.Revision AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.agentInstanceId') IS i.InstanceId AND ((json_extract(NEW.PayloadJson, '$.Key') IS 'instance.config_changed' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentInstanceId','revision','changedSections','actor')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.revision') = i.Revision AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.actor') IS 'guardedWrite' AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') IS 'array' AND json_array_length(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') BETWEEN 1 AND 4 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$.changedSections') c WHERE c.value NOT IN ('persona','unattendedModel','executionBudgets','harnessPolicy'))) OR (json_extract(NEW.PayloadJson, '$.Key') IS 'harness.definition_adopted' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('agentInstanceId','definitionId','previousVersion','activeVersion')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.definitionId') IS i.DefinitionId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.activeVersion') = i.ActiveVersion AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousVersion') >= 1 AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.previousVersion') <> i.ActiveVersion)) AND json_type(NEW.PayloadJson, '$.RootAgentRunId') IS 'null' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') = 0 AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') = 0)) OR (json_extract(NEW.PayloadJson, '$.Key') IS 'email.message.received' AND (json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson'), '$') WHERE key IN ('accountId','accountRef','messageRef','provider')) = 4) AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId')) = 36 AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') = lower(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId')) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),9,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),14,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),19,1) = '-' AND substr(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),24,1) = '-' AND replace(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId'),'-','') <> '00000000000000000000000000000000' AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef')) BETWEEN 1 AND 64 AND json_type(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef')) BETWEEN 1 AND 512 AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.DataJson')) field WHERE instr(field.value,char(0)) > 0 OR instr(field.value,char(10)) > 0 OR instr(field.value,char(13)) > 0) AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.provider') IN ('Gmail','Custom') AND NEW.EmailWatchEventId IS NEW.EventId AND json_type(NEW.PayloadJson, '$.RootAgentRunId') IS 'null' AND json_extract(NEW.PayloadJson, '$.TriggerDepth') = 0 AND json_array_length(NEW.PayloadJson, '$.VisitedAutomationIds') = 0 AND EXISTS (SELECT 1 FROM EmailWatches w JOIN TenantCredentials own ON own.CredentialId = w.AccountId AND own.TenantId = w.TenantId JOIN Credentials c ON c.CredentialId = own.CredentialId JOIN EmailAccountProfiles p ON p.CredentialId = c.CredentialId AND p.TenantId = own.TenantId JOIN Tenants tenant ON tenant.TenantId = own.TenantId JOIN AgentCredentialBindings b ON b.TenantId = own.TenantId AND b.CredentialId = own.CredentialId JOIN AgentInstances i ON i.InstanceId = b.AgentInstanceId JOIN SystemIdentities identity ON identity.TenantId = own.TenantId AND identity.AgentInstanceId = i.InstanceId WHERE w.TenantId = NEW.TenantId AND w.AccountId IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountId') AND own.DeletedAt IS NULL AND c.Purpose = 'EmailAccount' AND c.Status = 'Active' AND length(c.ProtectedPayload) > 0 AND tenant.Status = 0 AND w.Enabled = 1 AND json_extract(w.StateJson, '$.Paused') = 0 AND json_type(w.StateJson, '$.Cursor') IS 'object' AND json_extract(w.StateJson, '$.LastCommitHadCursor') = 1 AND json_extract(w.StateJson, '$.LastCommitRuntimeRevision') + 1 = w.RuntimeRevision AND json_extract(w.StateJson, '$.AuthorizationRevision') = c.Revision AND CAST(round((julianday(json_extract(w.StateJson, '$.LastSuccessAtUtc'))-2440587.5)*86400000) AS INTEGER) = NEW.ReceivedAtUtc AND b.AgentInstanceId IS NEW.AgentInstanceId AND b.Reference IS json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.accountRef') AND i.Lifecycle = 'Active' AND identity.Kind = 0 AND identity.PrincipalId = NEW.ExecutorPrincipalId AND json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.provider') IS CASE json_extract(p.ProfileJson, '$.Preset') WHEN 0 THEN 'Gmail' WHEN 1 THEN 'Custom' END AND (json_extract(p.ProfileJson, '$.AuthMethod') = 0 OR json_extract(p.ProfileJson, '$.ConnectionStatus') = 1) AND NEW.DedupeKey IS 'email.message.received:' || replace(lower(NEW.TenantId),'-','') || ':' || replace(w.AccountId,'-','') || ':' || json_extract(w.StateJson, '$.Generation') || ':' || json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.messageRef') || ':' || replace(NEW.AgentInstanceId,'-','')))) AND json_type(NEW.SubscriptionsJson) IS 'array' AND json_array_length(NEW.SubscriptionsJson) <= 1024 AND json_array_length(NEW.SubscriptionsJson) = (SELECT count(*) FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.ReceivedAtUtc) AND t.Enabled = 1 AND t.Kind = 2 AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND t.CoreEventKey = json_extract(NEW.PayloadJson, '$.Key')) AND (SELECT count(DISTINCT json_extract(value, '$.TriggerId')) FROM json_each(NEW.SubscriptionsJson)) = json_array_length(NEW.SubscriptionsJson) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT COALESCE(((json_type(f.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(f.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(f.value, '$.Owner.AgentInstanceId') IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(json_extract(f.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) IS lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) AND json_type(f.value, '$.Owner.profileId') IS 'null' AND (json_type(f.value, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(f.value, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(f.value, '$.TriggerRevision') >= 1 AND (json_type(f.value, '$.FilterExpression') IS 'null' OR (json_type(f.value, '$.FilterExpression') IS 'text' AND length(json_extract(f.value, '$.FilterExpression')) <= 1024)) AND ((json_extract(f.value, '$.Dispatch.Mode') = 0 AND json_type(f.value, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(f.value, '$.Dispatch.Mode') = 1 AND json_extract(f.value, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(f.value, '$.SourceKind') = 3 AND json_extract(f.value, '$.ResourceId') IS NULL AND EXISTS (SELECT 1 FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.ReceivedAtUtc) AND t.Enabled = 1 AND t.Kind = 2 AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND t.CoreEventKey = json_extract(NEW.PayloadJson, '$.Key') AND a.AutomationId IS json_extract(f.value, '$.AutomationId') AND a.AgentInstanceId IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(a.ExecutorPrincipalId) IS json_extract(f.value, '$.Owner.ExecutorPrincipalId') AND t.TriggerId IS json_extract(f.value, '$.TriggerId') AND t.Revision = json_extract(f.value, '$.TriggerRevision') AND t.FilterExpression IS json_extract(f.value, '$.FilterExpression') AND t.DispatchMode = json_extract(f.value, '$.Dispatch.Mode') AND t.DispatchWindowSeconds IS json_extract(f.value, '$.Dispatch.WindowSeconds'))),0)))),0);
END;
CREATE TRIGGER TR_TenantCore_Fact_Update BEFORE UPDATE ON CoreEvents
BEGIN
SELECT RAISE(ABORT, 'tenant Core facts and recipients are immutable') WHERE OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL;
SELECT RAISE(ABORT, 'legacy Core cannot conceal a tenant owner') WHERE NOT COALESCE((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.SubscriptionsJson IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)),0);
END;
CREATE TRIGGER TR_TenantCore_RunCausation_Insert BEFORE INSERT ON CoreEvents
WHEN NEW.TenantId IS NOT NULL AND json_extract(NEW.PayloadJson, '$.Key') IN ('run.completed','run.failed')
BEGIN
SELECT RAISE(ABORT, 'Core Run causation requires its actual accepted root') WHERE NOT COALESCE((EXISTS (SELECT 1 FROM AgentRuns source JOIN AgentRuns root ON root.ActivationId = source.RootActivationId AND root.TenantId = source.TenantId AND root.AgentInstanceId = source.AgentInstanceId AND root.ExecutorPrincipalId = source.ExecutorPrincipalId LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId = root.AgentRunId AND o.ExecutionSessionId = root.SessionId AND o.TenantId = root.TenantId AND o.AgentInstanceId = root.AgentInstanceId AND o.ExecutorPrincipalId = root.ExecutorPrincipalId AND o.Disposition = 6 WHERE source.TenantId = NEW.TenantId AND source.AgentRunId = json_extract(json_extract(NEW.PayloadJson, '$.DataJson'), '$.agentRunId') AND root.RootActivationId = root.ActivationId AND ((json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0 AND json_extract(root.PayloadJson, '$.admission.activation.kind') = 0) OR (o.SourceKind IN (0,1,2,3) AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = CASE o.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END AND json_extract(root.PayloadJson, '$.admission.activation.triggerOccurrenceId') IS o.OccurrenceId)) AND json_extract(NEW.PayloadJson, '$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson, '$.triggerContext.causation.rootAgentRunId'),root.AgentRunId) AND json_extract(NEW.PayloadJson, '$.TriggerDepth') = coalesce(json_extract(o.EvidenceJson, '$.triggerContext.causation.triggerDepth'),0) AND json_extract(NEW.PayloadJson, '$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson, '$.triggerContext.causation.visitedAutomationIds'),'[]'))),0);
END;
CREATE TRIGGER TR_TenantEmail_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.SourceKind = 3 AND EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') source JOIN CoreEvents fact ON fact.EventId IS source.value AND fact.TenantId = NEW.TenantId WHERE fact.EmailWatchEventId IS NOT NULL)
BEGIN
SELECT RAISE(ABORT, 'email occurrence evidence requires its literal included source facts') WHERE NOT COALESCE((NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') source WHERE NOT EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.EventId IS source.value AND fact.TenantId = NEW.TenantId AND fact.EmailWatchEventId IS fact.EventId AND json_extract(fact.PayloadJson, '$.Key') IS 'email.message.received')) AND ((json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 0 AND EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS NEW.SourceEventId AND json(json_extract(fact.PayloadJson, '$.DataJson')) IS json(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.source')))) OR (json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.sources') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.count') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext'), '$.sources') retained WHERE NOT COALESCE(((json_type(retained.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(retained.value, '$')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(retained.value, '$') WHERE key IN ('eventId','data')) = 2) AND json_extract(retained.value, '$.eventId') IS json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[' || retained.key || ']') AND EXISTS (SELECT 1 FROM CoreEvents fact WHERE fact.TenantId = NEW.TenantId AND fact.EventId IS json_extract(retained.value, '$.eventId') AND json(json_extract(fact.PayloadJson, '$.DataJson')) IS json(json_extract(retained.value, '$.data')))),0))))),0);
END;
CREATE TRIGGER TR_TenantEmail_Proof_Delete BEFORE DELETE ON EmailWatchEvents
WHEN OLD.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant email proofs are retained');
END;
CREATE TRIGGER TR_TenantEmail_Proof_Insert BEFORE INSERT ON EmailWatchEvents
BEGIN
SELECT RAISE(ABORT, 'email fact requires its exact completed lease and account proof') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.AccountId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.AuthorityJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.AuthorityJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents f WHERE f.EventId = NEW.EventId AND f.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM TenantCredentials own WHERE own.CredentialId IS json_extract(NEW.AuthorityJson, '$.AccountId'))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.AuthorityJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.AuthorityJson, '$')) = 9 AND (SELECT count(DISTINCT key) FROM json_each(NEW.AuthorityJson, '$') WHERE key IN ('Event','BindingId','BindingRevision','AccountId','Generation','AuthorizationFingerprint','WatchLeaseId','WatchRuntimeRevision','AccountRevision')) = 9) AND json_extract(NEW.AuthorityJson, '$.AccountId') IS NEW.AccountId AND length(json_extract(NEW.AuthorityJson, '$.BindingId')) = 36 AND json_extract(NEW.AuthorityJson, '$.BindingId') = lower(json_extract(NEW.AuthorityJson, '$.BindingId')) AND json_extract(NEW.AuthorityJson, '$.BindingId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.AuthorityJson, '$.BindingId'),9,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.BindingId'),14,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.BindingId'),19,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.BindingId'),24,1) = '-' AND replace(json_extract(NEW.AuthorityJson, '$.BindingId'),'-','') <> '00000000000000000000000000000000' AND json_extract(NEW.AuthorityJson, '$.BindingRevision') >= 1 AND length(json_extract(NEW.AuthorityJson, '$.WatchLeaseId')) = 36 AND json_extract(NEW.AuthorityJson, '$.WatchLeaseId') = lower(json_extract(NEW.AuthorityJson, '$.WatchLeaseId')) AND json_extract(NEW.AuthorityJson, '$.WatchLeaseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.AuthorityJson, '$.WatchLeaseId'),9,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.WatchLeaseId'),14,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.WatchLeaseId'),19,1) = '-' AND substr(json_extract(NEW.AuthorityJson, '$.WatchLeaseId'),24,1) = '-' AND replace(json_extract(NEW.AuthorityJson, '$.WatchLeaseId'),'-','') <> '00000000000000000000000000000000' AND json_extract(NEW.AuthorityJson, '$.WatchRuntimeRevision') >= 1 AND json_extract(NEW.AuthorityJson, '$.AccountRevision') >= 1 AND length(json_extract(NEW.AuthorityJson, '$.AuthorizationFingerprint')) = 64 AND json_extract(NEW.AuthorityJson, '$.AuthorizationFingerprint') NOT GLOB '*[^0-9A-F]*' AND EXISTS (SELECT 1 FROM CoreEvents f JOIN EmailWatches w ON w.TenantId = f.TenantId AND w.AccountId = NEW.AccountId JOIN AgentCredentialBindings b ON b.TenantId = f.TenantId AND b.BindingId IS json_extract(NEW.AuthorityJson, '$.BindingId') JOIN Credentials c ON c.CredentialId = NEW.AccountId WHERE f.TenantId = NEW.TenantId AND f.EventId IS NEW.EventId AND f.EmailWatchEventId IS NEW.EventId AND json(f.PayloadJson) IS json(json_extract(NEW.AuthorityJson, '$.Event')) AND json_extract(f.PayloadJson, '$.Key') IS 'email.message.received' AND json_extract(f.PayloadJson, '$.DataJson') IS json_extract(json_extract(NEW.AuthorityJson, '$.Event'), '$.DataJson') AND json_extract(json_extract(f.PayloadJson, '$.DataJson'), '$.accountId') IS NEW.AccountId AND b.CredentialId IS NEW.AccountId AND b.AgentInstanceId IS f.AgentInstanceId AND b.Revision = json_extract(NEW.AuthorityJson, '$.BindingRevision') AND b.Reference IS json_extract(json_extract(f.PayloadJson, '$.DataJson'), '$.accountRef') AND json_extract(NEW.AuthorityJson, '$.Generation') = json_extract(w.StateJson, '$.Generation') AND json_extract(NEW.AuthorityJson, '$.AuthorizationFingerprint') IS json_extract(w.StateJson, '$.AuthorizationFingerprint') AND json_extract(NEW.AuthorityJson, '$.WatchLeaseId') IS json_extract(w.StateJson, '$.LastCommitLeaseId') AND json_extract(NEW.AuthorityJson, '$.WatchRuntimeRevision') = json_extract(w.StateJson, '$.LastCommitRuntimeRevision') AND json_extract(NEW.AuthorityJson, '$.WatchRuntimeRevision') + 1 = w.RuntimeRevision AND json_extract(w.StateJson, '$.LastCommitHadCursor') = 1 AND json_extract(NEW.AuthorityJson, '$.AccountRevision') = c.Revision AND json_extract(NEW.AuthorityJson, '$.AccountRevision') = json_extract(w.StateJson, '$.AuthorizationRevision') AND CAST(round((julianday(json_extract(w.StateJson, '$.LastSuccessAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.ReceivedAtUtc AND w.Enabled = 1 AND json_extract(w.StateJson, '$.Paused') = 0))),0);
END;
CREATE TRIGGER TR_TenantEmail_Proof_Update BEFORE UPDATE ON EmailWatchEvents
BEGIN
SELECT RAISE(ABORT, 'tenant email proofs are immutable') WHERE OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL;
SELECT RAISE(ABORT, 'legacy proof cannot conceal tenant authority') WHERE NOT COALESCE((NEW.TenantId IS NULL AND NEW.AccountId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.AuthorityJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.AuthorityJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM CoreEvents f WHERE f.EventId = NEW.EventId AND f.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM TenantCredentials own WHERE own.CredentialId IS json_extract(NEW.AuthorityJson, '$.AccountId'))),0);
END;
CREATE TRIGGER TR_TenantEmail_Watch_Delete BEFORE DELETE ON EmailWatches
WHEN OLD.TenantId IS NOT NULL AND EXISTS (SELECT 1 FROM TenantCredentials owner WHERE owner.TenantId = OLD.TenantId AND owner.CredentialId = OLD.AccountId AND owner.DeletedAt IS NULL)
BEGIN
SELECT RAISE(ABORT, 'tenant email source state is retained');
END;
CREATE TRIGGER TR_TenantEmail_Watch_Insert BEFORE INSERT ON EmailWatches
BEGIN
SELECT RAISE(ABORT, 'watch requires a closed actual account owner and runtime') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.StateJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM TenantCredentials own WHERE own.CredentialId = NEW.AccountId)) OR (NEW.TenantId IS NOT NULL AND json_valid(NEW.StateJson) AND json_type(NEW.StateJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.StateJson)) = (SELECT count(DISTINCT key) FROM json_each(NEW.StateJson)) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.StateJson) WHERE key NOT IN ('AccountId','Enabled','IntervalSeconds','ConfigurationRevision','Generation','RuntimeRevision','Cursor','AuthorizationFingerprint','LastCheckedAtUtc','LastSuccessAtUtc','LastEventAtUtc','NextCheckAtUtc','LastGapAtUtc','LastErrorCode','LastDiagnosticId','ConsecutiveFailures','Paused','LeaseId','LeaseExpiresAtUtc','RecoveryCheckRequested','TenantId','AuthorizationRevision','LastCommitLeaseId','LastCommitRuntimeRevision','LastCommitHadCursor')) AND (SELECT count(*) FROM json_each(NEW.StateJson) WHERE key IN ('AccountId','Enabled','IntervalSeconds','ConfigurationRevision','Generation','RuntimeRevision','Cursor','AuthorizationFingerprint','LastCheckedAtUtc','LastSuccessAtUtc','LastEventAtUtc','NextCheckAtUtc','LastGapAtUtc','LastErrorCode','LastDiagnosticId','ConsecutiveFailures','Paused','LeaseId','LeaseExpiresAtUtc','RecoveryCheckRequested','TenantId')) = 21 AND lower(json_extract(NEW.StateJson, '$.TenantId')) IS lower(NEW.TenantId) AND json_extract(NEW.StateJson, '$.AccountId') IS NEW.AccountId AND length(NEW.AccountId) = 36 AND NEW.AccountId = lower(NEW.AccountId) AND NEW.AccountId NOT GLOB '*[^0-9a-f-]*' AND substr(NEW.AccountId,9,1) = '-' AND substr(NEW.AccountId,14,1) = '-' AND substr(NEW.AccountId,19,1) = '-' AND substr(NEW.AccountId,24,1) = '-' AND replace(NEW.AccountId,'-','') <> '00000000000000000000000000000000' AND json_extract(NEW.StateJson, '$.Enabled') = NEW.Enabled AND json_extract(NEW.StateJson, '$.RuntimeRevision') = NEW.RuntimeRevision AND NEW.RuntimeRevision >= 1 AND json_extract(NEW.StateJson, '$.ConfigurationRevision') >= 1 AND json_extract(NEW.StateJson, '$.Generation') >= 0 AND json_extract(NEW.StateJson, '$.IntervalSeconds') IN (60,300,900) AND json_extract(NEW.StateJson, '$.ConsecutiveFailures') BETWEEN 0 AND 20 AND json_type(NEW.StateJson, '$.Enabled') IN ('true','false') AND json_type(NEW.StateJson, '$.Paused') IN ('true','false') AND json_type(NEW.StateJson, '$.RecoveryCheckRequested') IN ('true','false') AND json_type(NEW.StateJson, '$.ConfigurationRevision') IS 'integer' AND json_type(NEW.StateJson, '$.Generation') IS 'integer' AND json_type(NEW.StateJson, '$.RuntimeRevision') IS 'integer' AND json_type(NEW.StateJson, '$.IntervalSeconds') IS 'integer' AND json_type(NEW.StateJson, '$.ConsecutiveFailures') IS 'integer' AND (json_type(NEW.StateJson, '$.LastCheckedAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastCheckedAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastCheckedAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastSuccessAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastSuccessAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastSuccessAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastEventAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastEventAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastEventAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.NextCheckAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.NextCheckAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.NextCheckAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastGapAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastGapAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastGapAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LeaseExpiresAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND NEW.NextCheckAtUtc = max(coalesce(CAST(round((julianday(json_extract(NEW.StateJson, '$.NextCheckAtUtc'))-2440587.5)*86400000) AS INTEGER),9223372036854775807),coalesce(CAST(round((julianday(json_extract(NEW.StateJson, '$.LeaseExpiresAtUtc'))-2440587.5)*86400000) AS INTEGER),0)) AND ((json_type(NEW.StateJson, '$.LeaseId') IS 'null' AND json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'null') OR (json_type(NEW.StateJson, '$.LeaseId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LeaseId')) = 36 AND json_extract(NEW.StateJson, '$.LeaseId') = lower(json_extract(NEW.StateJson, '$.LeaseId')) AND json_extract(NEW.StateJson, '$.LeaseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LeaseId'),'-','') <> '00000000000000000000000000000000' AND json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'text' AND json_type(NEW.StateJson, '$.AuthorizationRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.AuthorizationRevision') >= 1)) AND (json_type(NEW.StateJson, '$.AuthorizationFingerprint') IS 'null' OR (json_type(NEW.StateJson, '$.AuthorizationFingerprint') IS 'text' AND length(json_extract(NEW.StateJson, '$.AuthorizationFingerprint')) = 64 AND json_extract(NEW.StateJson, '$.AuthorizationFingerprint') NOT GLOB '*[^0-9A-F]*')) AND (json_type(NEW.StateJson, '$.AuthorizationRevision') IS NULL OR (json_type(NEW.StateJson, '$.AuthorizationRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.AuthorizationRevision') >= 1)) AND (json_type(NEW.StateJson, '$.LastErrorCode') IS 'null' OR (json_type(NEW.StateJson, '$.LastErrorCode') IS 'text' AND json_extract(NEW.StateJson, '$.LastErrorCode') IN ('email_watch_fanout_limit','email_watch_host_unavailable','email_watch_access_required','email_watch_temporary_failure'))) AND (json_type(NEW.StateJson, '$.LastDiagnosticId') IS 'null' OR (json_type(NEW.StateJson, '$.LastDiagnosticId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LastDiagnosticId')) = 36 AND json_extract(NEW.StateJson, '$.LastDiagnosticId') = lower(json_extract(NEW.StateJson, '$.LastDiagnosticId')) AND json_extract(NEW.StateJson, '$.LastDiagnosticId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LastDiagnosticId'),'-','') <> '00000000000000000000000000000000')) AND ((json_type(NEW.StateJson, '$.LastCommitLeaseId') IS NULL AND json_type(NEW.StateJson, '$.LastCommitRuntimeRevision') IS NULL AND json_type(NEW.StateJson, '$.LastCommitHadCursor') IS NULL) OR (json_type(NEW.StateJson, '$.LastCommitLeaseId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LastCommitLeaseId')) = 36 AND json_extract(NEW.StateJson, '$.LastCommitLeaseId') = lower(json_extract(NEW.StateJson, '$.LastCommitLeaseId')) AND json_extract(NEW.StateJson, '$.LastCommitLeaseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),'-','') <> '00000000000000000000000000000000' AND json_type(NEW.StateJson, '$.LastCommitRuntimeRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.LastCommitRuntimeRevision') >= 1 AND json_extract(NEW.StateJson, '$.LastCommitRuntimeRevision') < NEW.RuntimeRevision AND json_type(NEW.StateJson, '$.LastCommitHadCursor') IN ('true','false'))) AND (json_type(NEW.StateJson, '$.Cursor') IS 'null' OR ((json_type(json_extract(NEW.StateJson, '$.Cursor'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.StateJson, '$.Cursor'), '$')) = 6 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.StateJson, '$.Cursor'), '$') WHERE key IN ('Kind','HistoryId','UidValidity','LastUid','PageToken','Offset')) = 6) AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.Kind') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.Kind') IN (0,1,2) AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.Offset') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.Offset') >= 0 AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.UidValidity') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.UidValidity') BETWEEN 0 AND 4294967295 AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.LastUid') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.LastUid') BETWEEN 0 AND 4294967295 AND (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId') IS 'null' OR (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId') IS 'text' AND length(json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId')) <= 32)) AND (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken') IS 'null' OR (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken') IS 'text' AND length(json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken')) <= 2048)) AND length(CAST(json_extract(NEW.StateJson, '$.Cursor') AS BLOB)) <= 4096)) AND EXISTS (SELECT 1 FROM TenantCredentials own JOIN Credentials c ON c.CredentialId = own.CredentialId JOIN EmailAccountProfiles p ON p.CredentialId = c.CredentialId AND p.TenantId = own.TenantId WHERE own.TenantId = NEW.TenantId AND own.CredentialId IS NEW.AccountId AND own.DeletedAt IS NULL AND c.Purpose = 'EmailAccount'))),0);
END;
CREATE TRIGGER TR_TenantEmail_Watch_Update BEFORE UPDATE ON EmailWatches
BEGIN
SELECT RAISE(ABORT, 'email watch owner is immutable') WHERE OLD.TenantId IS NOT NEW.TenantId OR OLD.AccountId IS NOT NEW.AccountId;
SELECT RAISE(ABORT, 'email watch runtime must advance exactly once') WHERE NEW.TenantId IS NOT NULL AND NEW.RuntimeRevision <> OLD.RuntimeRevision + 1;
SELECT RAISE(ABORT, 'watch requires a closed actual account owner and runtime') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.StateJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM TenantCredentials own WHERE own.CredentialId = NEW.AccountId)) OR (NEW.TenantId IS NOT NULL AND json_valid(NEW.StateJson) AND json_type(NEW.StateJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.StateJson)) = (SELECT count(DISTINCT key) FROM json_each(NEW.StateJson)) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.StateJson) WHERE key NOT IN ('AccountId','Enabled','IntervalSeconds','ConfigurationRevision','Generation','RuntimeRevision','Cursor','AuthorizationFingerprint','LastCheckedAtUtc','LastSuccessAtUtc','LastEventAtUtc','NextCheckAtUtc','LastGapAtUtc','LastErrorCode','LastDiagnosticId','ConsecutiveFailures','Paused','LeaseId','LeaseExpiresAtUtc','RecoveryCheckRequested','TenantId','AuthorizationRevision','LastCommitLeaseId','LastCommitRuntimeRevision','LastCommitHadCursor')) AND (SELECT count(*) FROM json_each(NEW.StateJson) WHERE key IN ('AccountId','Enabled','IntervalSeconds','ConfigurationRevision','Generation','RuntimeRevision','Cursor','AuthorizationFingerprint','LastCheckedAtUtc','LastSuccessAtUtc','LastEventAtUtc','NextCheckAtUtc','LastGapAtUtc','LastErrorCode','LastDiagnosticId','ConsecutiveFailures','Paused','LeaseId','LeaseExpiresAtUtc','RecoveryCheckRequested','TenantId')) = 21 AND lower(json_extract(NEW.StateJson, '$.TenantId')) IS lower(NEW.TenantId) AND json_extract(NEW.StateJson, '$.AccountId') IS NEW.AccountId AND length(NEW.AccountId) = 36 AND NEW.AccountId = lower(NEW.AccountId) AND NEW.AccountId NOT GLOB '*[^0-9a-f-]*' AND substr(NEW.AccountId,9,1) = '-' AND substr(NEW.AccountId,14,1) = '-' AND substr(NEW.AccountId,19,1) = '-' AND substr(NEW.AccountId,24,1) = '-' AND replace(NEW.AccountId,'-','') <> '00000000000000000000000000000000' AND json_extract(NEW.StateJson, '$.Enabled') = NEW.Enabled AND json_extract(NEW.StateJson, '$.RuntimeRevision') = NEW.RuntimeRevision AND NEW.RuntimeRevision >= 1 AND json_extract(NEW.StateJson, '$.ConfigurationRevision') >= 1 AND json_extract(NEW.StateJson, '$.Generation') >= 0 AND json_extract(NEW.StateJson, '$.IntervalSeconds') IN (60,300,900) AND json_extract(NEW.StateJson, '$.ConsecutiveFailures') BETWEEN 0 AND 20 AND json_type(NEW.StateJson, '$.Enabled') IN ('true','false') AND json_type(NEW.StateJson, '$.Paused') IN ('true','false') AND json_type(NEW.StateJson, '$.RecoveryCheckRequested') IN ('true','false') AND json_type(NEW.StateJson, '$.ConfigurationRevision') IS 'integer' AND json_type(NEW.StateJson, '$.Generation') IS 'integer' AND json_type(NEW.StateJson, '$.RuntimeRevision') IS 'integer' AND json_type(NEW.StateJson, '$.IntervalSeconds') IS 'integer' AND json_type(NEW.StateJson, '$.ConsecutiveFailures') IS 'integer' AND (json_type(NEW.StateJson, '$.LastCheckedAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastCheckedAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastCheckedAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastSuccessAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastSuccessAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastSuccessAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastEventAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastEventAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastEventAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.NextCheckAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.NextCheckAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.NextCheckAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LastGapAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LastGapAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LastGapAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND (json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'null' OR (json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'text' AND CAST(round((julianday(json_extract(NEW.StateJson, '$.LeaseExpiresAtUtc'))-2440587.5)*86400000) AS INTEGER) IS NOT NULL)) AND NEW.NextCheckAtUtc = max(coalesce(CAST(round((julianday(json_extract(NEW.StateJson, '$.NextCheckAtUtc'))-2440587.5)*86400000) AS INTEGER),9223372036854775807),coalesce(CAST(round((julianday(json_extract(NEW.StateJson, '$.LeaseExpiresAtUtc'))-2440587.5)*86400000) AS INTEGER),0)) AND ((json_type(NEW.StateJson, '$.LeaseId') IS 'null' AND json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'null') OR (json_type(NEW.StateJson, '$.LeaseId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LeaseId')) = 36 AND json_extract(NEW.StateJson, '$.LeaseId') = lower(json_extract(NEW.StateJson, '$.LeaseId')) AND json_extract(NEW.StateJson, '$.LeaseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LeaseId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LeaseId'),'-','') <> '00000000000000000000000000000000' AND json_type(NEW.StateJson, '$.LeaseExpiresAtUtc') IS 'text' AND json_type(NEW.StateJson, '$.AuthorizationRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.AuthorizationRevision') >= 1)) AND (json_type(NEW.StateJson, '$.AuthorizationFingerprint') IS 'null' OR (json_type(NEW.StateJson, '$.AuthorizationFingerprint') IS 'text' AND length(json_extract(NEW.StateJson, '$.AuthorizationFingerprint')) = 64 AND json_extract(NEW.StateJson, '$.AuthorizationFingerprint') NOT GLOB '*[^0-9A-F]*')) AND (json_type(NEW.StateJson, '$.AuthorizationRevision') IS NULL OR (json_type(NEW.StateJson, '$.AuthorizationRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.AuthorizationRevision') >= 1)) AND (json_type(NEW.StateJson, '$.LastErrorCode') IS 'null' OR (json_type(NEW.StateJson, '$.LastErrorCode') IS 'text' AND json_extract(NEW.StateJson, '$.LastErrorCode') IN ('email_watch_fanout_limit','email_watch_host_unavailable','email_watch_access_required','email_watch_temporary_failure'))) AND (json_type(NEW.StateJson, '$.LastDiagnosticId') IS 'null' OR (json_type(NEW.StateJson, '$.LastDiagnosticId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LastDiagnosticId')) = 36 AND json_extract(NEW.StateJson, '$.LastDiagnosticId') = lower(json_extract(NEW.StateJson, '$.LastDiagnosticId')) AND json_extract(NEW.StateJson, '$.LastDiagnosticId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastDiagnosticId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LastDiagnosticId'),'-','') <> '00000000000000000000000000000000')) AND ((json_type(NEW.StateJson, '$.LastCommitLeaseId') IS NULL AND json_type(NEW.StateJson, '$.LastCommitRuntimeRevision') IS NULL AND json_type(NEW.StateJson, '$.LastCommitHadCursor') IS NULL) OR (json_type(NEW.StateJson, '$.LastCommitLeaseId') IS 'text' AND length(json_extract(NEW.StateJson, '$.LastCommitLeaseId')) = 36 AND json_extract(NEW.StateJson, '$.LastCommitLeaseId') = lower(json_extract(NEW.StateJson, '$.LastCommitLeaseId')) AND json_extract(NEW.StateJson, '$.LastCommitLeaseId') NOT GLOB '*[^0-9a-f-]*' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),9,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),14,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),19,1) = '-' AND substr(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),24,1) = '-' AND replace(json_extract(NEW.StateJson, '$.LastCommitLeaseId'),'-','') <> '00000000000000000000000000000000' AND json_type(NEW.StateJson, '$.LastCommitRuntimeRevision') IS 'integer' AND json_extract(NEW.StateJson, '$.LastCommitRuntimeRevision') >= 1 AND json_extract(NEW.StateJson, '$.LastCommitRuntimeRevision') < NEW.RuntimeRevision AND json_type(NEW.StateJson, '$.LastCommitHadCursor') IN ('true','false'))) AND (json_type(NEW.StateJson, '$.Cursor') IS 'null' OR ((json_type(json_extract(NEW.StateJson, '$.Cursor'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.StateJson, '$.Cursor'), '$')) = 6 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.StateJson, '$.Cursor'), '$') WHERE key IN ('Kind','HistoryId','UidValidity','LastUid','PageToken','Offset')) = 6) AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.Kind') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.Kind') IN (0,1,2) AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.Offset') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.Offset') >= 0 AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.UidValidity') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.UidValidity') BETWEEN 0 AND 4294967295 AND json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.LastUid') IS 'integer' AND json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.LastUid') BETWEEN 0 AND 4294967295 AND (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId') IS 'null' OR (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId') IS 'text' AND length(json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.HistoryId')) <= 32)) AND (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken') IS 'null' OR (json_type(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken') IS 'text' AND length(json_extract(json_extract(NEW.StateJson, '$.Cursor'), '$.PageToken')) <= 2048)) AND length(CAST(json_extract(NEW.StateJson, '$.Cursor') AS BLOB)) <= 4096)) AND EXISTS (SELECT 1 FROM TenantCredentials own JOIN Credentials c ON c.CredentialId = own.CredentialId JOIN EmailAccountProfiles p ON p.CredentialId = c.CredentialId AND p.TenantId = own.TenantId WHERE own.TenantId = NEW.TenantId AND own.CredentialId IS NEW.AccountId AND own.DeletedAt IS NULL AND c.Purpose = 'EmailAccount'))),0);
END;
CREATE TRIGGER TR_TenantEvent_Bucket_Delete BEFORE DELETE ON CoreEventBuckets
WHEN OLD.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant event facts and coverage are retained');
END;
CREATE TRIGGER TR_TenantEvent_Bucket_Insert BEFORE INSERT ON CoreEventBuckets
BEGIN
SELECT RAISE(ABORT, 'bucket requires its exact bounded matched source facts') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.AgentInstanceId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND json_type(NEW.PayloadJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson)) IN (6,7) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson) WHERE key NOT IN ('BucketId','Subscription','DueAtUtc','Sources','Flushed','CompletionCode','ReservedOccurrenceId')) AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson)) = (SELECT count(*) FROM json_each(NEW.PayloadJson)) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.profileId') IS 'null' AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.TriggerRevision') >= 1 AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'null' OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression')) <= 1024)) AND ((json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 0 AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 1 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.PayloadJson, '$.Subscription.AutomationId') IS NEW.AutomationId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerId') IS NEW.TriggerId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerRevision') = NEW.TriggerRevision AND json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.Mode') = 1 AND json_extract(NEW.PayloadJson, '$.Flushed') = NEW.Flushed AND json_extract(NEW.PayloadJson, '$.ReservedOccurrenceId') IS NEW.ReservedOccurrenceId AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.DueAtUtc')) - 2440587.5)*86400000) AS INTEGER) = NEW.DueAtUtc AND json_type(NEW.PayloadJson, '$.Sources') IS 'array' AND json_array_length(NEW.PayloadJson, '$.Sources') BETWEEN 1 AND 24 AND (SELECT count(DISTINCT json_extract(value, '$.EventId')) FROM json_each(NEW.PayloadJson, '$.Sources')) = json_array_length(NEW.PayloadJson, '$.Sources') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Sources') e WHERE NOT COALESCE(((json_type(e.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$')) = 8 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$') WHERE key IN ('EventId','Owner','Key','ReceivedAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 8) AND (json_type(e.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(e.value, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(e.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(e.value, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(e.value, '$.Owner.profileId') IS 'null' AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) < NEW.DueAtUtc AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) >= NEW.DueAtUtc - 1000 * json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.WindowSeconds') AND ((json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d JOIN CoreEvents f ON f.EventId = d.EventId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (1,6) AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS json_extract(f.PayloadJson, '$.Key') AND json_extract(e.value, '$.DataJson') IS json_extract(f.PayloadJson, '$.DataJson') AND json_extract(e.value, '$.RootAgentRunId') IS json_extract(f.PayloadJson, '$.RootAgentRunId') AND json_extract(e.value, '$.TriggerDepth') IS json_extract(f.PayloadJson, '$.TriggerDepth') AND json_extract(e.value, '$.VisitedAutomationIds') IS json_extract(f.PayloadJson, '$.VisitedAutomationIds') AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.ReceivedAtUtc)) OR (json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents f ON f.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = f.ResourceId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (0,5) AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS w.EventKey AND json(json_extract(e.value, '$.DataJson')) IS json_extract(f.EvidenceJson, '$.data') AND json_type(e.value, '$.RootAgentRunId') IS 'null' AND json_extract(e.value, '$.TriggerDepth') = 0 AND json_array_length(e.value, '$.VisitedAutomationIds') = 0 AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.AdmittedAtUtc))) AND NOT EXISTS (SELECT 1 FROM CoreEventBuckets existing, json_each(existing.PayloadJson, '$.Sources') covered WHERE existing.TenantId = NEW.TenantId AND existing.TriggerId = NEW.TriggerId AND existing.BucketId <> NEW.BucketId AND json_extract(covered.value, '$.EventId') IS json_extract(e.value, '$.EventId'))),0)) AND (NEW.ReservedOccurrenceId IS NULL OR EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.BucketId AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') AND ((NEW.Flushed = 0 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Flushed = 1 AND ((o.Disposition = 6 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'null') OR (o.Disposition = 4 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'text')))))))),0);
END;
CREATE TRIGGER TR_TenantEvent_Bucket_Update BEFORE UPDATE ON CoreEventBuckets
BEGIN
SELECT RAISE(ABORT, 'tenant bucket identity is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.BucketId IS NOT NEW.BucketId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.DueAtUtc IS NOT NEW.DueAtUtc);
SELECT RAISE(ABORT, 'reserved or flushed bucket membership is immutable') WHERE OLD.TenantId IS NOT NULL AND ((OLD.ReservedOccurrenceId IS NOT NULL OR OLD.Flushed = 1) AND (json_extract(OLD.PayloadJson, '$.Sources') IS NOT json_extract(NEW.PayloadJson, '$.Sources') OR json_extract(OLD.PayloadJson, '$.Subscription') IS NOT json_extract(NEW.PayloadJson, '$.Subscription')) OR OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Flushed = 1 AND OLD.PayloadJson IS NOT NEW.PayloadJson);
SELECT RAISE(ABORT, 'bucket requires its exact bounded matched source facts') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.AgentInstanceId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.PayloadJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL)) OR (NEW.TenantId IS NOT NULL AND json_type(NEW.PayloadJson) IS 'object' AND (SELECT count(*) FROM json_each(NEW.PayloadJson)) IN (6,7) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson) WHERE key NOT IN ('BucketId','Subscription','DueAtUtc','Sources','Flushed','CompletionCode','ReservedOccurrenceId')) AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson)) = (SELECT count(*) FROM json_each(NEW.PayloadJson)) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Owner.profileId') IS 'null' AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.TriggerRevision') >= 1 AND (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'null' OR (json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression') IS 'text' AND length(json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.FilterExpression')) <= 1024)) AND ((json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 0 AND json_type(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.Mode') = 1 AND json_extract(json_extract(NEW.PayloadJson, '$.Subscription'), '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.PayloadJson, '$.Subscription.AutomationId') IS NEW.AutomationId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerId') IS NEW.TriggerId AND json_extract(NEW.PayloadJson, '$.Subscription.TriggerRevision') = NEW.TriggerRevision AND json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.Mode') = 1 AND json_extract(NEW.PayloadJson, '$.Flushed') = NEW.Flushed AND json_extract(NEW.PayloadJson, '$.ReservedOccurrenceId') IS NEW.ReservedOccurrenceId AND CAST(round((julianday(json_extract(NEW.PayloadJson, '$.DueAtUtc')) - 2440587.5)*86400000) AS INTEGER) = NEW.DueAtUtc AND json_type(NEW.PayloadJson, '$.Sources') IS 'array' AND json_array_length(NEW.PayloadJson, '$.Sources') BETWEEN 1 AND 24 AND (SELECT count(DISTINCT json_extract(value, '$.EventId')) FROM json_each(NEW.PayloadJson, '$.Sources')) = json_array_length(NEW.PayloadJson, '$.Sources') AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PayloadJson, '$.Sources') e WHERE NOT COALESCE(((json_type(e.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$')) = 8 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$') WHERE key IN ('EventId','Owner','Key','ReceivedAtUtc','DataJson','RootAgentRunId','TriggerDepth','VisitedAutomationIds')) = 8) AND (json_type(e.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(e.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(e.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(e.value, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(e.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(e.value, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(e.value, '$.Owner.profileId') IS 'null' AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) < NEW.DueAtUtc AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) >= NEW.DueAtUtc - 1000 * json_extract(NEW.PayloadJson, '$.Subscription.Dispatch.WindowSeconds') AND ((json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d JOIN CoreEvents f ON f.EventId = d.EventId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (1,6) AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS json_extract(f.PayloadJson, '$.Key') AND json_extract(e.value, '$.DataJson') IS json_extract(f.PayloadJson, '$.DataJson') AND json_extract(e.value, '$.RootAgentRunId') IS json_extract(f.PayloadJson, '$.RootAgentRunId') AND json_extract(e.value, '$.TriggerDepth') IS json_extract(f.PayloadJson, '$.TriggerDepth') AND json_extract(e.value, '$.VisitedAutomationIds') IS json_extract(f.PayloadJson, '$.VisitedAutomationIds') AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.ReceivedAtUtc)) OR (json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents f ON f.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = f.ResourceId WHERE d.TenantId = NEW.TenantId AND d.EventId = json_extract(e.value, '$.EventId') AND d.TriggerId = NEW.TriggerId AND d.Status IN (0,5) AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json(d.SnapshotJson) IS json_extract(NEW.PayloadJson, '$.Subscription') AND json_extract(e.value, '$.Key') IS w.EventKey AND json(json_extract(e.value, '$.DataJson')) IS json_extract(f.EvidenceJson, '$.data') AND json_type(e.value, '$.RootAgentRunId') IS 'null' AND json_extract(e.value, '$.TriggerDepth') = 0 AND json_array_length(e.value, '$.VisitedAutomationIds') = 0 AND CAST(round((julianday(json_extract(e.value, '$.ReceivedAtUtc'))-2440587.5)*86400000) AS INTEGER) = f.AdmittedAtUtc))) AND NOT EXISTS (SELECT 1 FROM CoreEventBuckets existing, json_each(existing.PayloadJson, '$.Sources') covered WHERE existing.TenantId = NEW.TenantId AND existing.TriggerId = NEW.TriggerId AND existing.BucketId <> NEW.BucketId AND json_extract(covered.value, '$.EventId') IS json_extract(e.value, '$.EventId'))),0)) AND (NEW.ReservedOccurrenceId IS NULL OR EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.BucketId AND o.SourceKind = json_extract(NEW.PayloadJson, '$.Subscription.SourceKind') AND ((NEW.Flushed = 0 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Flushed = 1 AND ((o.Disposition = 6 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'null') OR (o.Disposition = 4 AND json_type(NEW.PayloadJson, '$.CompletionCode') IS 'text')))))))),0);
END;
CREATE TRIGGER TR_TenantEvent_Child_Update BEFORE UPDATE ON AutomationTriggers
WHEN EXISTS (SELECT 1 FROM Automations a WHERE a.AutomationId IN (OLD.AutomationId,NEW.AutomationId) AND a.TenantId IS NOT NULL)
BEGIN
SELECT RAISE(ABORT, 'tenant child source is immutable and revisions are exact') WHERE NOT COALESCE((OLD.TriggerId IS NEW.TriggerId AND OLD.AutomationId IS NEW.AutomationId AND OLD.Kind IS NEW.Kind AND OLD.EventId IS NEW.EventId AND OLD.CoreEventKey IS NEW.CoreEventKey AND NEW.Revision = OLD.Revision + CASE WHEN OLD.Enabled IS NEW.Enabled AND OLD.FilterExpression IS NEW.FilterExpression AND OLD.DispatchMode IS NEW.DispatchMode AND OLD.DispatchWindowSeconds IS NEW.DispatchWindowSeconds AND OLD.ScheduleJson IS NEW.ScheduleJson THEN 0 ELSE 1 END),0);
END;
CREATE TRIGGER TR_TenantEvent_Occurrence_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL AND NEW.SourceKind IN (1,3)
BEGIN
SELECT RAISE(ABORT, 'event occurrence requires its actual matched receipt or bucket') WHERE NOT COALESCE((json_type(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource')) IS 'object' AND (SELECT count(*) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) = CASE NEW.SourceKind WHEN 1 THEN 10 ELSE 8 END AND (SELECT count(DISTINCT lower(key)) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) = (SELECT count(*) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'))) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource')) WHERE key NOT IN ('TenantId','TriggerId','SourceKind','SourceId','IsBucket','SourceHash','EventIds','DedupeKey','ResourceRevision','ResourceId')) AND lower(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.TenantId')) IS lower(NEW.TenantId) AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.TriggerId') IS NEW.TriggerId AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceKind') = NEW.SourceKind AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceId') IS NEW.SourceEventId AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.DedupeKey') IS NEW.DedupeKey AND length(json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceHash')) = 64 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.SourceHash') NOT GLOB '*[^0-9a-f]*' AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') BETWEEN 1 AND 24 AND ((json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 0 AND ((NEW.SourceKind = 3 AND EXISTS (SELECT 1 FROM CoreEventDeliveries d WHERE d.TenantId = NEW.TenantId AND d.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND d.AgentInstanceId = NEW.AgentInstanceId AND d.AutomationId = NEW.AutomationId AND d.TriggerId = NEW.TriggerId AND d.EventId = NEW.SourceEventId AND d.Status = 1 AND d.ReservedOccurrenceId IS NULL AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = 1 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[0]') IS d.EventId)) OR (NEW.SourceKind = 1 AND EXISTS (SELECT 1 FROM ExternalEventDeliveries d JOIN ExternalEvents e ON e.EventId = d.EventId JOIN WebhookEvents w ON w.ResourceId = e.ResourceId WHERE d.TenantId = NEW.TenantId AND d.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND d.AgentInstanceId = NEW.AgentInstanceId AND d.AutomationId = NEW.AutomationId AND d.TriggerId = NEW.TriggerId AND d.EventId = NEW.SourceEventId AND d.Status = 0 AND d.ReservedOccurrenceId IS NULL AND json_extract(d.DecisionJson, '$.Matched') = 1 AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = 1 AND json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[0]') IS d.EventId AND w.TenantId = NEW.TenantId AND w.Status = 0 AND w.ResourceId IS json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.ResourceId') AND w.Revision = json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.ResourceRevision'))))) OR (json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.IsBucket') = 1 AND EXISTS (SELECT 1 FROM CoreEventBuckets b WHERE b.TenantId = NEW.TenantId AND b.AgentInstanceId = NEW.AgentInstanceId AND b.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND b.AutomationId = NEW.AutomationId AND b.TriggerId = NEW.TriggerId AND b.BucketId = NEW.SourceEventId AND b.Flushed = 0 AND b.ReservedOccurrenceId IS NULL AND b.DueAtUtc <= NEW.AdmittedAtUtc AND json_extract(b.PayloadJson, '$.Subscription.SourceKind') = NEW.SourceKind AND ((json_array_length(b.PayloadJson, '$.Sources') = json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') AND NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') e WHERE json_extract(e.value, '$.EventId') IS NOT json_extract(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds[' || e.key || ']'))) OR (NEW.SourceKind = 3 AND json_array_length(b.PayloadJson, '$.Sources') > 0 AND NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') e WHERE json_extract(e.value, '$.Key') IS NOT 'email.message.received') AND json_array_length(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') = (SELECT count(DISTINCT value) FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds')) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') retained WHERE NOT EXISTS (SELECT 1 FROM json_each(b.PayloadJson, '$.Sources') original WHERE json_extract(original.value, '$.EventId') IS retained.value)) AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') earlier JOIN json_each(json_extract(NEW.EvidenceJson, '$.triggerContext.admissionSource'), '$.EventIds') later ON earlier.key < later.key JOIN json_each(b.PayloadJson, '$.Sources') originalEarlier ON json_extract(originalEarlier.value, '$.EventId') IS earlier.value JOIN json_each(b.PayloadJson, '$.Sources') originalLater ON json_extract(originalLater.value, '$.EventId') IS later.value WHERE originalEarlier.key >= originalLater.key))))))),0);
END;
CREATE TRIGGER TR_TenantExperience_Delete BEFORE DELETE ON TenantExperiences
BEGIN SELECT RAISE(ABORT,'Retain Experience provenance through a content-free tombstone'); END;
CREATE TRIGGER TR_TenantExperience_Insert BEFORE INSERT ON TenantExperiences
WHEN NEW.Revision<>1 OR NEW.Visibility<>0 OR NEW.CreatedAtUtc<>NEW.UpdatedAtUtc
    OR NOT EXISTS (SELECT 1 FROM AgentRuns run JOIN AgentRuns root ON root.AgentRunId=NEW.RootRunId
        WHERE run.AgentRunId=NEW.RunId AND run.TenantId=NEW.TenantId AND run.SessionId=NEW.SessionId
            AND run.AgentInstanceId=NEW.AgentInstanceId AND run.ExecutorPrincipalId=NEW.ExecutorPrincipalId
            AND root.TenantId=run.TenantId AND root.AgentInstanceId=run.AgentInstanceId AND root.ExecutorPrincipalId=run.ExecutorPrincipalId
            AND root.ActivationId=run.RootActivationId AND root.RootActivationId=root.ActivationId
            AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND root.OriginalInitiatorPrincipalId=NEW.UserPrincipalId
                AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId=NEW.TenantId AND u.UserId=NEW.UserId AND u.PrincipalId=NEW.UserPrincipalId AND u.AccountId=NEW.AccountId))
                OR (json_extract(root.PayloadJson,'$.admission.executionContext.origin')<>0 AND NEW.UserPrincipalId IS NULL AND NEW.AccountId IS NULL AND NEW.UserId IS NULL)))
    OR (SELECT count(*) FROM TenantExperiences e WHERE e.TenantId=NEW.TenantId AND e.AgentInstanceId=NEW.AgentInstanceId AND e.OwnerKey=NEW.OwnerKey AND e.Visibility<>2)>=500
    OR (NEW.SourceKind IN (0,1) AND (
        json_type(NEW.SourceWitnessJson,'$.Kind') IS NOT 'integer' OR json_extract(NEW.SourceWitnessJson,'$.Kind') IS NOT NEW.SourceKind
        OR json_type(NEW.SourceWitnessJson,'$.SourceId') IS NOT 'text' OR json_extract(NEW.SourceWitnessJson,'$.SourceId') IS NOT lower(NEW.SourceId)
        OR json_type(NEW.SourceWitnessJson,'$.ThroughCursor') IS NOT 'integer' OR json_extract(NEW.SourceWitnessJson,'$.ThroughCursor') IS NOT NEW.ThroughCursor
        OR json_type(NEW.SourceWitnessJson,'$.SessionId') IS NOT 'text'
        OR json_type(NEW.SourceWitnessJson,'$.ObservationHash') IS NOT 'text' OR length(json_extract(NEW.SourceWitnessJson,'$.ObservationHash'))<>64
        OR json_extract(NEW.SourceWitnessJson,'$.ObservationHash') GLOB '*[^0-9a-f]*'
        OR NOT EXISTS (SELECT 1 FROM TenantSessions source JOIN Sessions saved ON saved.SessionId=source.SessionId
            WHERE source.TenantId=NEW.TenantId AND source.AgentInstanceId=NEW.AgentInstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                AND source.SessionId=json_extract(NEW.SourceWitnessJson,'$.SessionId') AND saved.DurablyDeletedAtUtc IS NULL)
        OR (NEW.SourceKind=0 AND (json_extract(NEW.SourceWitnessJson,'$.SessionId') IS NOT lower(NEW.SourceId)
            OR json_type(NEW.SourceWitnessJson,'$.SessionRevision') IS NOT 'integer'
            OR json_type(NEW.SourceWitnessJson,'$.AfterEntrySequence') IS NOT 'integer' OR json_extract(NEW.SourceWitnessJson,'$.AfterEntrySequence')<0
            OR json_extract(NEW.SourceWitnessJson,'$.AfterEntrySequence')>=NEW.ThroughCursor
            OR json_type(NEW.SourceWitnessJson,'$.Limit') IS NOT 'integer' OR json_extract(NEW.SourceWitnessJson,'$.Limit') NOT BETWEEN 1 AND 20
            OR json_extract(NEW.SourceWitnessJson,'$.SourceRunId') IS NOT NULL OR json_extract(NEW.SourceWitnessJson,'$.SourceRunRevision') IS NOT NULL
            OR json_extract(NEW.SourceWitnessJson,'$.PrivateUserPrincipalId') IS NOT NULL OR lower(NEW.SourceId)=NEW.SessionId
            OR NOT EXISTS (SELECT 1 FROM Sessions saved JOIN SessionSnapshots snapshot ON snapshot.SessionId=saved.SessionId
                WHERE saved.SessionId=lower(NEW.SourceId) AND saved.Revision=json_extract(NEW.SourceWitnessJson,'$.SessionRevision') AND snapshot.LastEntrySequence>=NEW.ThroughCursor)))
        OR (NEW.SourceKind=1 AND (json_extract(NEW.SourceWitnessJson,'$.SessionRevision') IS NOT 0 OR json_extract(NEW.SourceWitnessJson,'$.AfterEntrySequence') IS NOT 0
            OR json_extract(NEW.SourceWitnessJson,'$.Limit') IS NOT 0 OR json_extract(NEW.SourceWitnessJson,'$.SourceRunId') IS NOT lower(NEW.SourceId)
            OR json_extract(NEW.SourceWitnessJson,'$.SourceRunRevision') IS NOT NEW.ThroughCursor OR lower(NEW.SourceId)=NEW.RunId
            OR (json_extract(NEW.SourceWitnessJson,'$.PrivateUserPrincipalId') IS NOT NULL AND json_extract(NEW.SourceWitnessJson,'$.PrivateUserPrincipalId') IS NOT lower(NEW.UserPrincipalId))
            OR NOT EXISTS (SELECT 1 FROM AgentRuns source WHERE source.AgentRunId=lower(NEW.SourceId) AND source.TenantId=NEW.TenantId
                AND source.AgentInstanceId=NEW.AgentInstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND source.Revision=NEW.ThroughCursor
                AND source.SessionId=json_extract(NEW.SourceWitnessJson,'$.SessionId'))))))
    OR (NEW.SourceKind=2 AND (
        (SELECT count(DISTINCT value) FROM json_each(NEW.SourceExperienceIdsJson))<>json_array_length(NEW.SourceExperienceIdsJson)
        OR EXISTS (SELECT 1 FROM json_each(NEW.SourceExperienceIdsJson) parent WHERE parent.type IS NOT 'text' OR parent.value=lower(NEW.ExperienceId)
            OR NOT EXISTS (SELECT 1 FROM TenantExperiences source WHERE source.TenantId=NEW.TenantId AND lower(source.ExperienceId)=parent.value
                AND source.AgentInstanceId=NEW.AgentInstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND source.OwnerKey=NEW.OwnerKey AND source.Visibility IN (0,3)))))
BEGIN SELECT RAISE(ABORT,'Experience requires exact actual-root ownership, retained source witnesses and bounded lineage'); END;
CREATE TRIGGER TR_TenantExperience_Update BEFORE UPDATE ON TenantExperiences
WHEN NEW.TenantId IS NOT OLD.TenantId OR NEW.ExperienceId IS NOT OLD.ExperienceId OR NEW.AgentInstanceId IS NOT OLD.AgentInstanceId
    OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId OR NEW.OwnerKey IS NOT OLD.OwnerKey OR NEW.UserPrincipalId IS NOT OLD.UserPrincipalId
    OR NEW.AccountId IS NOT OLD.AccountId OR NEW.UserId IS NOT OLD.UserId OR NEW.SessionId IS NOT OLD.SessionId OR NEW.RunId IS NOT OLD.RunId OR NEW.RootRunId IS NOT OLD.RootRunId
    OR NEW.SourceKind IS NOT OLD.SourceKind OR NEW.SourceId IS NOT OLD.SourceId OR NEW.ThroughCursor IS NOT OLD.ThroughCursor OR NEW.SourceWitnessJson IS NOT OLD.SourceWitnessJson
    OR NEW.ContentHash IS NOT OLD.ContentHash OR NEW.SourceExperienceIdsJson IS NOT OLD.SourceExperienceIdsJson OR NEW.CreatedAtUtc IS NOT OLD.CreatedAtUtc
    OR NEW.Revision<>OLD.Revision+1 OR NEW.UpdatedAtUtc<OLD.UpdatedAtUtc OR OLD.Visibility=2 OR (OLD.Visibility=3 AND NEW.Visibility<>2)
    OR (NEW.Visibility<>2 AND NEW.ContentJson IS NOT OLD.ContentJson) OR (NEW.Visibility=2 AND NEW.ContentJson IS NOT NULL)
BEGIN SELECT RAISE(ABORT,'Retain immutable Experience provenance, exact revisions and terminal tombstones'); END;
CREATE TRIGGER TR_TenantLearnedMemory_Delete BEFORE DELETE ON TenantLearnedMemories
BEGIN SELECT RAISE(ABORT, 'Tombstone learned memory without discarding provenance'); END;
CREATE TRIGGER TR_TenantLearnedMemory_Insert BEFORE INSERT ON TenantLearnedMemories
WHEN NEW.Revision <> 1 OR NEW.Status <> 0 OR NEW.CreatedAtUtc <> NEW.UpdatedAtUtc
    OR NEW.SubjectKey = '' OR length(NEW.SubjectKey) > 128 OR NEW.ContentHash GLOB '*[^0-9a-f]*'
    OR NEW.OwnerKey IS NOT CASE NEW.Scope
        WHEN 0 THEN 'session:' || NEW.SessionId || ':' || lower(coalesce(NEW.UserPrincipalId, NEW.ExecutorPrincipalId))
        WHEN 1 THEN 'identityUser:' || NEW.AgentInstanceId || ':' || lower(NEW.UserPrincipalId)
        WHEN 2 THEN 'user:' || lower(NEW.UserPrincipalId) END
    OR (SELECT count(*) FROM TenantLearnedMemories m WHERE m.TenantId = NEW.TenantId AND m.OwnerKey = NEW.OwnerKey AND m.Status = 0) >= 256
    OR NOT EXISTS (SELECT 1 FROM AgentRuns r JOIN AgentRuns root ON root.AgentRunId = NEW.RootRunId
        WHERE r.AgentRunId = NEW.RunId AND r.TenantId = NEW.TenantId AND r.SessionId = NEW.SessionId
        AND r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND r.Status = 1
        AND root.TenantId = NEW.TenantId AND root.AgentInstanceId = NEW.AgentInstanceId AND root.ExecutorPrincipalId = NEW.ExecutorPrincipalId
        AND r.RootActivationId = root.ActivationId AND root.RootActivationId = root.ActivationId
        AND ((NEW.UserPrincipalId IS NULL AND NEW.SourceEntryId IS NULL AND NEW.Source = 'agentInferred'
              AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') <> 0)
            OR (root.OriginalInitiatorPrincipalId = NEW.UserPrincipalId
              AND json_extract(root.PayloadJson, '$.admission.executionContext.origin') = 0
              AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = NEW.UserPrincipalId
                  AND u.AccountId = NEW.AccountId AND u.UserId = NEW.UserId)
              AND EXISTS (SELECT 1 FROM ActivationSourceEntries e JOIN ConversationEntries c ON c.EntryId = e.EntryId AND c.SessionId = e.SessionId
                  WHERE e.ActivationId = root.ActivationId AND e.EntryId = NEW.SourceEntryId AND c.Role = 'User'))))
    OR (NEW.SupersedesMemoryId IS NOT NULL AND NOT EXISTS (SELECT 1 FROM TenantLearnedMemories old
        WHERE old.TenantId = NEW.TenantId AND old.MemoryId = NEW.SupersedesMemoryId AND old.OwnerKey = NEW.OwnerKey
        AND old.Kind = NEW.Kind AND old.SubjectKey = NEW.SubjectKey AND old.Status = 1))
BEGIN SELECT RAISE(ABORT, 'Learned memory requires its actual owned Run, User and source'); END;
CREATE TRIGGER TR_TenantLearnedMemory_Lineage_Insert BEFORE INSERT ON TenantLearnedMemories
WHEN NEW.SourceMemoryIdsJson IS NOT NULL AND (
    (SELECT count(DISTINCT value) FROM json_each(NEW.SourceMemoryIdsJson)) <> json_array_length(NEW.SourceMemoryIdsJson)
    OR EXISTS (SELECT 1 FROM json_each(NEW.SourceMemoryIdsJson) source WHERE source.type <> 'text' OR NOT EXISTS (
        SELECT 1 FROM TenantLearnedMemories m WHERE m.TenantId = NEW.TenantId AND lower(m.MemoryId) = source.value
            AND m.OwnerKey = NEW.OwnerKey AND m.Scope = NEW.Scope AND m.Kind = NEW.Kind AND m.Status = 1
            AND m.UserPrincipalId IS NEW.UserPrincipalId AND m.AccountId IS NEW.AccountId AND m.UserId IS NEW.UserId))
)
BEGIN SELECT RAISE(ABORT, 'Consolidation requires exact retained same-owner same-scope same-kind parents'); END;
CREATE TRIGGER TR_TenantLearnedMemory_Lineage_Update BEFORE UPDATE ON TenantLearnedMemories
WHEN OLD.SourceMemoryIdsJson IS NOT NEW.SourceMemoryIdsJson
BEGIN SELECT RAISE(ABORT, 'Retain immutable learned Memory lineage'); END;
CREATE TRIGGER TR_TenantLearnedMemory_Update BEFORE UPDATE ON TenantLearnedMemories
WHEN OLD.TenantId IS NOT NEW.TenantId OR OLD.MemoryId IS NOT NEW.MemoryId OR OLD.Scope IS NOT NEW.Scope OR OLD.OwnerKey IS NOT NEW.OwnerKey
    OR OLD.UserPrincipalId IS NOT NEW.UserPrincipalId OR OLD.AccountId IS NOT NEW.AccountId OR OLD.UserId IS NOT NEW.UserId
    OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId
    OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RunId IS NOT NEW.RunId OR OLD.RootRunId IS NOT NEW.RootRunId
    OR OLD.SourceEntryId IS NOT NEW.SourceEntryId OR OLD.Kind IS NOT NEW.Kind OR OLD.Source IS NOT NEW.Source
    OR OLD.ContentHash IS NOT NEW.ContentHash OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc OR OLD.SupersedesMemoryId IS NOT NEW.SupersedesMemoryId
    OR NEW.Revision <> OLD.Revision + 1 OR NEW.UpdatedAtUtc < OLD.UpdatedAtUtc
    OR NOT ((OLD.Status = 0 AND NEW.Status IN (1,2,3)) OR (OLD.Status IN (1,3) AND NEW.Status = 2))
    OR (NEW.Status = 2 AND (NEW.Subject <> '' OR NEW.Content <> '' OR NEW.SubjectKey <> ''))
    OR (NEW.Status <> 2 AND (OLD.Subject IS NOT NEW.Subject OR OLD.Content IS NOT NEW.Content OR OLD.SubjectKey IS NOT NEW.SubjectKey))
BEGIN SELECT RAISE(ABORT, 'Retain immutable learned memory ownership, values and revision'); END;
CREATE TRIGGER TR_TenantOccurrences_Source_Insert BEFORE INSERT ON TriggerOccurrences
WHEN NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence requires exact admission or pending consent') WHERE NOT COALESCE((
(json_extract(NEW.EvidenceJson, '$.admissionApproval') IS NULL AND EXISTS (
SELECT 1 FROM Automations automation JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
JOIN TenantSessions session ON session.SessionId = NEW.ExecutionSessionId
JOIN Activations activation ON activation.ActivationId = run.ActivationId
WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.SessionId = session.SessionId AND run.RootActivationId = run.ActivationId AND (NEW.SourceKind = 2 OR run.OriginalInitiatorPrincipalId IS NULL) AND run.ProfileId IS NULL
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.AgentInstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND (NEW.ExecutionTargetKind = 1 OR session.CreatedByPrincipalId = NEW.ExecutorPrincipalId) AND json_extract(activation.PayloadJson, '$.triggerOccurrenceId') = NEW.OccurrenceId
AND activation.RootActivationId = activation.ActivationId AND activation.DedupeKey = NEW.DedupeKey
AND NEW.ProfileId IS NULL AND NEW.SourceKind IN (0,1,2,3) AND NEW.Disposition = 6
AND json_extract(run.PayloadJson, '$.admission.outputContract') = CASE NEW.ExecutionTargetKind WHEN 0 THEN 1 WHEN 1 THEN 0 END
AND json_extract(run.PayloadJson, '$.admission.executionContext.origin') = CASE NEW.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END))
OR (NEW.Disposition = 3 AND NEW.DispositionReason = 'admission_approval_pending' AND NEW.RoutingRevision = 1
AND NEW.ExecutionSessionId IS NULL AND NEW.AcceptedAgentRunId IS NULL AND NEW.ProfileId IS NULL
AND NEW.SourceKind IN (0,1,2,3) AND json_type(NEW.EvidenceJson, '$.admissionApproval') IS 'object'
AND (SELECT count(*) FROM json_each(NEW.EvidenceJson, '$.admissionApproval')) = 7
AND (SELECT count(DISTINCT key) FROM json_each(NEW.EvidenceJson, '$.admissionApproval') WHERE key IN ('requestId','sessionId','runId','activationId','entryId','responseId','requesterPrincipalId')) = 7
AND json_type(NEW.EvidenceJson, '$.admissionApproval.requestId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.runId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.activationId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.entryId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId'),24,1) IS '-'
AND json_type(NEW.EvidenceJson, '$.admissionApproval.responseId') IS 'text' AND length(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')) = 36 AND json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId') NOT GLOB '*[^0-9a-f-]*' AND json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId') IS NOT '00000000-0000-0000-0000-000000000000' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),9,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),14,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),19,1) IS '-' AND substr(json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId'),24,1) IS '-'
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.runId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') IS NOT json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND EXISTS (SELECT 1 FROM Automations a WHERE a.AutomationId = NEW.AutomationId AND a.TenantId = NEW.TenantId
AND a.AgentInstanceId = NEW.AgentInstanceId AND a.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND a.ProfileId IS NULL)
AND NOT EXISTS (SELECT 1 FROM AgentRuns r WHERE r.AgentRunId = json_extract(NEW.EvidenceJson, '$.admissionApproval.runId'))
AND ((NEW.ExecutionTargetKind = 0 AND NOT EXISTS (SELECT 1 FROM TenantSessions s WHERE s.SessionId = json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')))
OR (NEW.ExecutionTargetKind = 1 AND NEW.ReportToSessionId IS NULL AND NEW.TargetSessionId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND EXISTS (SELECT 1 FROM TenantSessions s JOIN Sessions saved ON saved.SessionId = s.SessionId WHERE s.TenantId = NEW.TenantId AND s.AgentInstanceId = NEW.AgentInstanceId
AND s.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND s.SessionId IS NEW.TargetSessionId AND saved.DurablyDeletedAtUtc IS NULL AND json_extract(saved.OriginJson, '$.kind') = 0)))
AND ((NEW.SourceKind IN (0,1,3) AND json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId') IS NULL)
OR (NEW.SourceKind = 2 AND EXISTS (SELECT 1 FROM TenantUsers u WHERE u.TenantId = NEW.TenantId AND lower(u.PrincipalId) = json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId')))))),0);
END;
CREATE TRIGGER TR_TenantOccurrences_Source_Update BEFORE UPDATE ON TriggerOccurrences
WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant occurrence source is immutable') WHERE 1=1 AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.OccurrenceId IS NOT NEW.OccurrenceId OR OLD.AutomationId IS NOT NEW.AutomationId
OR OLD.DedupeKey IS NOT NEW.DedupeKey OR OLD.EvidenceJson IS NOT NEW.EvidenceJson OR OLD.SourceKind IS NOT NEW.SourceKind
OR OLD.TriggerRevision IS NOT NEW.TriggerRevision OR OLD.ScheduledAtUtc IS NOT NEW.ScheduledAtUtc OR OLD.ObservedAtUtc IS NOT NEW.ObservedAtUtc
OR OLD.AdmittedAtUtc IS NOT NEW.AdmittedAtUtc
OR OLD.SourceEventId IS NOT NEW.SourceEventId OR OLD.ModelOfferingId IS NOT NEW.ModelOfferingId OR OLD.ModelConnectionId IS NOT NEW.ModelConnectionId
OR OLD.ExternalModelId IS NOT NEW.ExternalModelId OR OLD.ModelReasoningEffort IS NOT NEW.ModelReasoningEffort
OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.ModelSource IS NOT NEW.ModelSource OR OLD.ExecutionTargetKind IS NOT NEW.ExecutionTargetKind
OR OLD.TargetSessionId IS NOT NEW.TargetSessionId OR OLD.ReportToSessionId IS NOT NEW.ReportToSessionId


OR OLD.ClaimId IS NOT NEW.ClaimId OR OLD.ClaimLeaseExpiresAtUtc IS NOT NEW.ClaimLeaseExpiresAtUtc
OR OLD.LiveSessionId IS NOT NEW.LiveSessionId OR OLD.LiveEvaluationCompletedAtUtc IS NOT NEW.LiveEvaluationCompletedAtUtc);
SELECT RAISE(ABORT, 'tenant occurrence permits only exact consent settlement') WHERE NOT (
OLD.ExecutionSessionId IS NEW.ExecutionSessionId AND OLD.AcceptedAgentRunId IS NEW.AcceptedAgentRunId
AND OLD.Disposition IS NEW.Disposition AND OLD.DispositionReason IS NEW.DispositionReason
AND OLD.RoutingRevision IS NEW.RoutingRevision AND OLD.RoutingUpdatedAtUtc IS NEW.RoutingUpdatedAtUtc)
AND NOT COALESCE((OLD.Disposition = 3 AND OLD.DispositionReason = 'admission_approval_pending' AND OLD.RoutingRevision = 1
AND NEW.RoutingRevision = 2 AND NEW.RoutingUpdatedAtUtc >= OLD.RoutingUpdatedAtUtc
AND EXISTS (SELECT 1 FROM TenantApprovalRequests r
WHERE lower(r.RequestId) = json_extract(NEW.EvidenceJson, '$.admissionApproval.requestId')
AND r.TenantId = NEW.TenantId AND r.InstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND r.AutomationId = NEW.AutomationId
AND ((NEW.Disposition = 4 AND r.Status IN (1,2,3,4) AND NEW.ExecutionSessionId IS NULL AND NEW.AcceptedAgentRunId IS NULL
AND NEW.DispositionReason IS NOT NULL)
OR (NEW.Disposition = 6 AND r.Status = 1 AND NEW.DispositionReason IS NULL
AND NEW.ExecutionSessionId = json_extract(NEW.EvidenceJson, '$.admissionApproval.sessionId')
AND NEW.AcceptedAgentRunId = json_extract(NEW.EvidenceJson, '$.admissionApproval.runId')
AND EXISTS (
SELECT 1 FROM Automations automation JOIN AgentRuns run ON run.AgentRunId = NEW.AcceptedAgentRunId
JOIN TenantSessions session ON session.SessionId = NEW.ExecutionSessionId
JOIN Activations activation ON activation.ActivationId = run.ActivationId
WHERE automation.TenantId = NEW.TenantId AND automation.AutomationId = NEW.AutomationId
AND automation.AgentInstanceId = NEW.AgentInstanceId AND automation.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.TenantId = NEW.TenantId AND run.AgentInstanceId = NEW.AgentInstanceId AND run.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND run.ActivationId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.activationId')
AND lower(run.OriginalInitiatorPrincipalId) IS json_extract(NEW.EvidenceJson, '$.admissionApproval.requesterPrincipalId')
AND json_extract(run.PayloadJson, '$.admission.responseId') IS json_extract(NEW.EvidenceJson, '$.admissionApproval.responseId')
AND json_extract(run.PayloadJson, '$.admission.taskIntent.text') IS json_extract(NEW.EvidenceJson, '$.instructions')
AND ((NEW.ExecutionTargetKind = 0 AND EXISTS (SELECT 1 FROM ActivationSourceEntries entry WHERE entry.ActivationId = run.ActivationId AND entry.SessionId = run.SessionId
    AND entry.EntryId IS json_extract(NEW.EvidenceJson, '$.admissionApproval.entryId') AND entry.Ordinal = 0))
OR (NEW.ExecutionTargetKind = 1 AND NOT EXISTS (SELECT 1 FROM ActivationSourceEntries entry WHERE entry.ActivationId = run.ActivationId)
AND json_extract(json_extract(run.PayloadJson, '$.admission.activation.evidenceJson'), '$.InputBoundarySequence') IS json_extract(r.PayloadJson, '$.Operation.AutomationAdmission.Conversation.InputBoundarySequence')))
AND run.SessionId = session.SessionId AND run.RootActivationId = run.ActivationId AND (NEW.SourceKind = 2 OR run.OriginalInitiatorPrincipalId IS NULL) AND run.ProfileId IS NULL
AND session.TenantId = NEW.TenantId AND session.AgentInstanceId = NEW.AgentInstanceId AND session.ExecutorPrincipalId = NEW.ExecutorPrincipalId
AND (NEW.ExecutionTargetKind = 1 OR session.CreatedByPrincipalId = NEW.ExecutorPrincipalId) AND json_extract(activation.PayloadJson, '$.triggerOccurrenceId') = NEW.OccurrenceId
AND activation.RootActivationId = activation.ActivationId AND activation.DedupeKey = NEW.DedupeKey
AND NEW.ProfileId IS NULL AND NEW.SourceKind IN (0,1,2,3) AND NEW.Disposition = 6
AND json_extract(run.PayloadJson, '$.admission.outputContract') = CASE NEW.ExecutionTargetKind WHEN 0 THEN 1 WHEN 1 THEN 0 END
AND json_extract(run.PayloadJson, '$.admission.executionContext.origin') = CASE NEW.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END))))),0);
END;
CREATE TRIGGER TR_TenantOperationReceipts_AutomationInvocation_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN json_extract(NEW.AutomationMutationJson,'$.ToolName')='automation.run' AND NOT EXISTS
 (SELECT 1 FROM TriggerOccurrences occurrence JOIN AgentRuns admitted ON admitted.AgentRunId=occurrence.AcceptedAgentRunId
  JOIN AgentRuns parent ON parent.AgentRunId=NEW.RunId
  WHERE occurrence.TenantId=NEW.TenantId AND occurrence.AgentInstanceId=NEW.InstanceId AND occurrence.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND occurrence.AutomationId=json_extract(NEW.AutomationMutationJson,'$.AutomationId') AND occurrence.SourceKind=2 AND occurrence.Disposition=6
  AND occurrence.OccurrenceId=json_extract(NEW.AutomationMutationJson,'$.InvocationOccurrenceId')
  AND occurrence.ExecutionSessionId=json_extract(NEW.AutomationMutationJson,'$.InvocationSessionId')
  AND occurrence.AcceptedAgentRunId=json_extract(NEW.AutomationMutationJson,'$.InvocationRunId')
  AND admitted.TenantId=NEW.TenantId AND admitted.AgentInstanceId=NEW.InstanceId AND admitted.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND admitted.SessionId=occurrence.ExecutionSessionId AND admitted.OriginalInitiatorPrincipalId=parent.OriginalInitiatorPrincipalId
  AND admitted.RootActivationId=admitted.ActivationId AND json_extract(admitted.PayloadJson,'$.admission.executionContext.origin')=7)
BEGIN SELECT RAISE(ABORT,'Manual invocation receipt requires its exact accepted owned occurrence and Run'); END;
CREATE TRIGGER TR_TenantOperationReceipts_AutomationMutation_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.AutomationMutationJson IS NOT NULL AND NOT EXISTS
 (SELECT 1 FROM Automations a WHERE a.TenantId=NEW.TenantId AND a.AgentInstanceId=NEW.InstanceId AND a.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND a.AutomationId=json_extract(NEW.AutomationMutationJson,'$.AutomationId') AND a.Revision=json_extract(NEW.AutomationMutationJson,'$.Revision'))
BEGIN SELECT RAISE(ABORT,'Automation mutation receipt requires exact applied owned parent'); END;
CREATE TRIGGER TR_TenantOperationReceipts_AutomationMutation_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN NEW.AutomationMutationJson IS NOT OLD.AutomationMutationJson
BEGIN SELECT RAISE(ABORT,'Retain immutable Automation mutation receipt'); END;
CREATE TRIGGER TR_TenantOperationReceipts_ConfigurationBinding_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.Outcome=0 AND EXISTS(SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=NEW.RequestId AND (json_type(request.PayloadJson,'$.Operation.ManagementMutation.Remedy')='object' OR json_type(request.PayloadJson,'$.Operation.ManagementMutation.Binding')='object'))
BEGIN SELECT RAISE(ABORT,'Configuration receipt requires its actual exact settings or binding effect') WHERE NOT EXISTS
 (SELECT 1 FROM TenantApprovalRequests request JOIN AgentInstances owner ON owner.InstanceId=request.InstanceId
 WHERE request.RequestId=NEW.RequestId AND request.OperationId=NEW.OperationId AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.InstanceId
 AND request.Status=1 AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND request.Fingerprint=NEW.ActionHash AND owner.Revision=NEW.InstanceRevision
 AND (json_type(request.PayloadJson,'$.Operation.ManagementMutation.Remedy')='object' AND NOT EXISTS(SELECT 1 FROM json_each(request.PayloadJson,'$.Operation.ManagementMutation.Settings') field
  WHERE json_type(owner.SettingsOverridesJson,'$.'||lower(substr(field.key,1,1))||substr(field.key,2)) IS NOT field.type
   OR json_extract(owner.SettingsOverridesJson,'$.'||lower(substr(field.key,1,1))||substr(field.key,2)||'.value') IS NOT json_extract(field.value,'$.Value'))
 OR json_type(request.PayloadJson,'$.Operation.ManagementMutation.Binding')='object' AND
  (json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=0 AND EXISTS(SELECT 1 FROM AgentCredentialBindings choice WHERE choice.TenantId=NEW.TenantId AND choice.AgentInstanceId=NEW.InstanceId
   AND choice.BindingId=json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId') AND choice.CredentialId=json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.CredentialId')
   AND choice.Reference=json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Reference') AND choice.Revision=json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.Revision'))
  OR json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Remove')=1 AND NOT EXISTS(SELECT 1 FROM AgentCredentialBindings choice WHERE choice.TenantId=NEW.TenantId AND choice.BindingId=json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Binding.Value.BindingId'))))); END;
CREATE TRIGGER TR_TenantOperationReceipts_ConfigurationBinding_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN EXISTS(SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=OLD.RequestId AND (json_type(request.PayloadJson,'$.Operation.ManagementMutation.Remedy')='object' OR json_type(request.PayloadJson,'$.Operation.ManagementMutation.Binding')='object'))
 AND (OLD.OperationId IS NOT NEW.OperationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ActionHash IS NOT NEW.ActionHash OR OLD.Outcome IS NOT NEW.Outcome OR OLD.ReasonCode IS NOT NEW.ReasonCode OR OLD.InstanceRevision IS NOT NEW.InstanceRevision OR OLD.PersonaRevision IS NOT NEW.PersonaRevision OR OLD.RecordedAt IS NOT NEW.RecordedAt OR OLD.SourceCallId IS NOT NEW.SourceCallId OR OLD.ParametersHash IS NOT NEW.ParametersHash OR OLD.BrowserArtifactsJson IS NOT NEW.BrowserArtifactsJson OR OLD.AutomationMutationJson IS NOT NEW.AutomationMutationJson OR OLD.ManagementSkillJson IS NOT NEW.ManagementSkillJson OR OLD.ManagementResourceJson IS NOT NEW.ManagementResourceJson)
BEGIN SELECT RAISE(ABORT,'Retain immutable configuration remedy and binding receipts'); END;
CREATE TRIGGER TR_TenantOperationReceipts_HarnessAssessment_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL OR NEW.Outcome=0 AND NEW.RequestId IS NOT NULL AND EXISTS
 (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=NEW.RequestId AND json_type(request.PayloadJson,'$.Operation.ManagementMutation.Harness')='object')
BEGIN SELECT RAISE(ABORT,'Harness assessment requires its exact factual owner effect') WHERE NOT EXISTS
 (SELECT 1 FROM AgentInstances owner,json_each(owner.HarnessManagementJson,'$.instanceChanges') assessment
 WHERE owner.InstanceId=NEW.InstanceId AND owner.Revision=NEW.InstanceRevision AND json_extract(assessment.value,'$.instanceRevision')=NEW.InstanceRevision
 AND json_extract(assessment.value,'$.definitionVersion')=owner.ActiveVersion AND json_extract(assessment.value,'$.evidence.status')=1 AND json_extract(assessment.value,'$.evidence.actor')='Agent' AND json_extract(assessment.value,'$.approvalId') IS NULL AND json_extract(assessment.value,'$.policyRevision')=COALESCE(json_extract(owner.HarnessManagementJson,'$.policyRevision'),1)
 AND (NEW.RequestId IS NULL AND json_extract(assessment.value,'$.operationId')=json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId')
  AND json_extract(assessment.value,'$.operation') IN ('knowledge.upsert','knowledge.remove')
 OR NEW.RequestId IS NOT NULL AND EXISTS (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=NEW.RequestId AND request.OperationId=NEW.OperationId
  AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.InstanceId AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId
  AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.OperationId') IS json_extract(assessment.value,'$.operationId') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Operation') IS json_extract(assessment.value,'$.operation') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.InstanceRevision') IS json_extract(assessment.value,'$.instanceRevision') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.DefinitionVersion') IS json_extract(assessment.value,'$.definitionVersion') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.PolicyRevision') IS json_extract(assessment.value,'$.policyRevision') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.ApprovalId') IS json_extract(assessment.value,'$.approvalId') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.ActionHash') IS json_extract(assessment.value,'$.actionHash') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Source') IS json_extract(assessment.value,'$.source') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.SavedAt') IS json_extract(assessment.value,'$.savedAt') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Actor') IS json_extract(assessment.value,'$.evidence.actor') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.DraftRevision') IS json_extract(assessment.value,'$.evidence.draftRevision') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Check') IS json_extract(assessment.value,'$.evidence.check') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Status') IS json_extract(assessment.value,'$.evidence.status') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Expected') IS json_extract(assessment.value,'$.evidence.expected') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Observed') IS json_extract(assessment.value,'$.evidence.observed') AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Harness.Assessment.Evidence.Limitation') IS json_extract(assessment.value,'$.evidence.limitation')
  AND (NEW.ManagementResourceJson IS NULL OR json_extract(assessment.value,'$.operationId')=json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId')))));
END;
CREATE TRIGGER TR_TenantOperationReceipts_HarnessAssessment_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN (json_extract(OLD.ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL OR json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL
 OR EXISTS(SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=OLD.RequestId AND json_type(request.PayloadJson,'$.Operation.ManagementMutation.Harness')='object'))
 AND (OLD.OperationId IS NOT NEW.OperationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ActionHash IS NOT NEW.ActionHash OR OLD.Outcome IS NOT NEW.Outcome OR OLD.ReasonCode IS NOT NEW.ReasonCode OR OLD.InstanceRevision IS NOT NEW.InstanceRevision OR OLD.PersonaRevision IS NOT NEW.PersonaRevision OR OLD.RecordedAt IS NOT NEW.RecordedAt OR OLD.SourceCallId IS NOT NEW.SourceCallId OR OLD.ParametersHash IS NOT NEW.ParametersHash OR OLD.BrowserArtifactsJson IS NOT NEW.BrowserArtifactsJson OR OLD.AutomationMutationJson IS NOT NEW.AutomationMutationJson OR OLD.ManagementSkillJson IS NOT NEW.ManagementSkillJson OR OLD.ManagementResourceJson IS NOT NEW.ManagementResourceJson)
BEGIN SELECT RAISE(ABORT,'Harness effect evidence is immutable'); END;
CREATE TRIGGER TR_TenantOperationReceipts_Http_DELETE BEFORE DELETE ON TenantOperationReceipts
WHEN OLD.HttpJson IS NOT NULL OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId=OLD.RequestId AND r.Permission='http.request')
BEGIN SELECT RAISE(ABORT,'HTTP receipts are retained'); END;
CREATE TRIGGER TR_TenantOperationReceipts_Http_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.HttpJson IS NOT NULL OR NEW.ReasonCode IN ('http_request_completed','http_request_uncertain','http_request_not_dispatched')
    OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId=NEW.RequestId AND r.Permission='http.request')
BEGIN
    SELECT RAISE(ABORT,'HTTP receipt requires its exact approved owned operation') WHERE NOT EXISTS
        (SELECT 1 FROM TenantApprovalRequests r JOIN TenantApprovalDecisions d ON d.RequestId=r.RequestId AND d.TenantId=r.TenantId
        WHERE r.RequestId=NEW.RequestId AND r.TenantId=NEW.TenantId AND r.Permission='http.request' AND r.Status=1
            AND r.OperationId=NEW.OperationId AND lower(r.InstanceId)=lower(NEW.InstanceId) AND r.SessionId=NEW.SessionId
            AND r.RootActivationId=NEW.RootActivationId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND json_extract(r.PayloadJson,'$.Operation.ActionHash')=NEW.ActionHash
            AND d.Status=1 AND d.ActionHash=NEW.ActionHash AND d.RequestRevision=r.Revision
            AND json_type(r.PayloadJson,'$.Operation.HttpRequest')='object');
    SELECT RAISE(ABORT,'Completed HTTP observation requires reserved native dispatch and factual metadata')
        WHERE NEW.Outcome=0 AND (NEW.HttpJson IS NULL OR NOT EXISTS
            (SELECT 1 FROM AgentRuns r WHERE r.AgentRunId=NEW.RunId AND r.TenantId=NEW.TenantId AND r.SessionId=NEW.SessionId
            AND r.AgentInstanceId=NEW.InstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND r.RootActivationId=NEW.RootActivationId
            AND json_extract(r.PayloadJson,'$.approvalRequestId')=lower(NEW.RequestId)
            AND json_extract(r.PayloadJson,'$.sideEffect.toolCallId')='operation:' || lower(NEW.OperationId)
            AND json_extract(r.PayloadJson,'$.sideEffect.actionHash')=NEW.ActionHash
            AND json_type(r.PayloadJson,'$.sideEffect.externalEffect')='object'));
END;
CREATE TRIGGER TR_TenantOperationReceipts_Http_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN OLD.HttpJson IS NOT NULL OR NEW.HttpJson IS NOT NULL
    OR EXISTS(SELECT 1 FROM TenantApprovalRequests r WHERE r.RequestId IN (OLD.RequestId,NEW.RequestId) AND r.Permission='http.request')
BEGIN SELECT RAISE(ABORT,'HTTP receipts are immutable'); END;
CREATE TRIGGER TR_TenantOperationReceipts_ManagementMutation_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.ReasonCode='management_mutation_completed' AND NEW.ManagementSkillJson IS NULL AND NEW.ManagementResourceJson IS NULL AND (NEW.Outcome<>0 OR NOT EXISTS
 (SELECT 1 FROM TenantApprovalRequests request JOIN AgentInstances owner ON owner.InstanceId=request.InstanceId
 WHERE request.RequestId=NEW.RequestId AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.InstanceId
 AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND request.RootActivationId=NEW.RootActivationId AND request.SessionId=NEW.SessionId
 AND request.Status=1 AND request.Kind IN (0,1) AND json_type(request.PayloadJson,'$.Operation.ManagementMutation')='object'
 AND lower(json_extract(request.PayloadJson,'$.Operation.OperationId'))=lower(NEW.OperationId) AND json_extract(request.PayloadJson,'$.Operation.ActionHash')=NEW.ActionHash
 AND json_extract(request.PayloadJson,'$.Operation.ExpectedInstanceRevision')+1=NEW.InstanceRevision AND owner.Revision=NEW.InstanceRevision))
BEGIN SELECT RAISE(ABORT,'Management mutation receipt requires its exact completed configuration'); END;
CREATE TRIGGER TR_TenantOperationReceipts_ManagementResource_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.ManagementResourceJson IS NOT NULL OR NEW.Outcome=0 AND NEW.ReasonCode='management_mutation_completed' AND EXISTS
 (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=NEW.RequestId AND json_type(request.PayloadJson,'$.Operation.ManagementMutation.Resource')='object')
BEGIN
 SELECT RAISE(ABORT,'Resource receipt requires exact owned factual witnesses') WHERE NOT COALESCE((
  NEW.ManagementResourceJson IS NOT NULL AND EXISTS (SELECT 1 FROM AgentInstances owner JOIN TenantSessions session ON session.AgentInstanceId=owner.InstanceId
   JOIN AgentRuns run ON run.AgentRunId=NEW.RunId AND run.SessionId=session.SessionId
   WHERE owner.InstanceId=NEW.InstanceId AND owner.Revision=NEW.InstanceRevision AND session.SessionId=NEW.SessionId
   AND session.TenantId=NEW.TenantId AND session.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND run.RootActivationId=NEW.RootActivationId)
  AND NOT EXISTS (SELECT 1 FROM json_each(NEW.ManagementResourceJson,'$.BeforeSources') source
   WHERE json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') IS NOT 'resource' OR json_type(source.value,'$.Key') IS NOT 'text'
   OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
   OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64
   OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*')
  AND NOT EXISTS (SELECT 1 FROM json_each(NEW.ManagementResourceJson,'$.AfterSources') source
   WHERE json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') IS NOT 'resource' OR json_type(source.value,'$.Key') IS NOT 'text'
   OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
   OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64
   OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*')
  AND (NEW.RequestId IS NULL AND NEW.SourceCallId IS NOT NULL AND NEW.ParametersHash IS NOT NULL
   AND json_extract(NEW.ManagementResourceJson,'$.ToolName')='resources.upsert' AND json_array_length(NEW.ManagementResourceJson,'$.BeforeSources')=0
   AND json_array_length(NEW.ManagementResourceJson,'$.AfterSources')=1 AND json_extract(NEW.ManagementResourceJson,'$.Result.Revision')=1
   OR NEW.RequestId IS NULL AND json_extract(NEW.ManagementResourceJson,'$.HarnessChangeId') IS NOT NULL
   OR NEW.RequestId IS NOT NULL AND EXISTS (SELECT 1 FROM TenantApprovalRequests request
    WHERE request.RequestId=NEW.RequestId AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.InstanceId
    AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND request.RootActivationId=NEW.RootActivationId AND request.SessionId=NEW.SessionId
    AND request.Status=1 AND request.Kind IN (0,1) AND request.OperationId=NEW.OperationId AND request.Fingerprint=NEW.ActionHash
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Resource.Changed')=1
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Resource.Result')=json_extract(NEW.ManagementResourceJson,'$.Result')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.ToolName')=json_extract(NEW.ManagementResourceJson,'$.ToolName')
    AND json_extract(request.PayloadJson,'$.Operation.ExpectedInstanceRevision')=json_extract(NEW.ManagementResourceJson,'$.BeforeInstanceRevision')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.BeforeStableHash')=json_extract(NEW.ManagementResourceJson,'$.BeforeStableHash')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.AfterStableHash')=json_extract(NEW.ManagementResourceJson,'$.AfterStableHash')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Resource.BeforeSources')=json_extract(NEW.ManagementResourceJson,'$.BeforeSources')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Resource.AfterSources')=json_extract(NEW.ManagementResourceJson,'$.AfterSources')))
 ),0);
END;
CREATE TRIGGER TR_TenantOperationReceipts_ManagementResource_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN (OLD.ManagementResourceJson IS NOT NULL OR NEW.ManagementResourceJson IS NOT NULL OR EXISTS
 (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId=OLD.RequestId AND json_type(request.PayloadJson,'$.Operation.ManagementMutation.Resource')='object'))
 AND (OLD.OperationId IS NOT NEW.OperationId OR OLD.RequestId IS NOT NEW.RequestId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.RunId IS NOT NEW.RunId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.RootActivationId IS NOT NEW.RootActivationId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.InstanceId IS NOT NEW.InstanceId OR OLD.ActionHash IS NOT NEW.ActionHash OR OLD.Outcome IS NOT NEW.Outcome OR OLD.ReasonCode IS NOT NEW.ReasonCode OR OLD.InstanceRevision IS NOT NEW.InstanceRevision OR OLD.PersonaRevision IS NOT NEW.PersonaRevision OR OLD.RecordedAt IS NOT NEW.RecordedAt OR OLD.SourceCallId IS NOT NEW.SourceCallId OR OLD.ParametersHash IS NOT NEW.ParametersHash OR OLD.BrowserArtifactsJson IS NOT NEW.BrowserArtifactsJson OR OLD.AutomationMutationJson IS NOT NEW.AutomationMutationJson OR OLD.ManagementSkillJson IS NOT NEW.ManagementSkillJson OR OLD.ManagementResourceJson IS NOT NEW.ManagementResourceJson)
BEGIN SELECT RAISE(ABORT,'Resource effect evidence is immutable'); END;
CREATE TRIGGER TR_TenantOperationReceipts_ManagementSkill_INSERT BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.ManagementSkillJson IS NOT NULL
BEGIN
 SELECT RAISE(ABORT,'Skill receipt requires exact owned factual witnesses') WHERE NOT COALESCE((
  EXISTS (SELECT 1 FROM AgentInstances owner JOIN TenantSessions session ON session.AgentInstanceId=owner.InstanceId
   JOIN AgentRuns run ON run.AgentRunId=NEW.RunId AND run.SessionId=session.SessionId
   WHERE owner.InstanceId=NEW.InstanceId AND owner.Revision=NEW.InstanceRevision AND session.SessionId=NEW.SessionId
   AND session.TenantId=NEW.TenantId AND session.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND run.RootActivationId=NEW.RootActivationId)
  AND NOT EXISTS (SELECT 1 FROM json_each(NEW.ManagementSkillJson,'$.BeforeSources') source
   WHERE json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') NOT IN ('skill','skillProcedure') OR json_type(source.value,'$.Key') IS NOT 'text'
   OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
   OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64
   OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*')
  AND NOT EXISTS (SELECT 1 FROM json_each(NEW.ManagementSkillJson,'$.AfterSources') source
   WHERE json_type(source.value,'$.Kind') IS NOT 'text' OR json_extract(source.value,'$.Kind') NOT IN ('skill','skillProcedure') OR json_type(source.value,'$.Key') IS NOT 'text'
   OR json_extract(source.value,'$.ResourceId') IS NOT (NEW.InstanceId||':'||json_extract(source.value,'$.Key'))
   OR json_type(source.value,'$.Fingerprint') IS NOT 'text' OR length(json_extract(source.value,'$.Fingerprint'))<>64
   OR json_extract(source.value,'$.Fingerprint') GLOB '*[^0-9a-f]*')
  AND (NEW.RequestId IS NULL AND NEW.SourceCallId IS NOT NULL AND NEW.ParametersHash IS NOT NULL
   AND json_extract(NEW.ManagementSkillJson,'$.ToolName') NOT IN ('skills.delete','skills.customize')
   OR NEW.RequestId IS NOT NULL AND EXISTS (SELECT 1 FROM TenantApprovalRequests request
    WHERE request.RequestId=NEW.RequestId AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.InstanceId
    AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND request.RootActivationId=NEW.RootActivationId AND request.SessionId=NEW.SessionId
    AND request.Status=1 AND request.Kind IN (0,1) AND request.OperationId=NEW.OperationId AND request.Fingerprint=NEW.ActionHash
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Skill.Changed')=1
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Skill.Result')=json_extract(NEW.ManagementSkillJson,'$.Result')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.ToolName')=json_extract(NEW.ManagementSkillJson,'$.ToolName')
    AND json_extract(request.PayloadJson,'$.Operation.ExpectedInstanceRevision')=json_extract(NEW.ManagementSkillJson,'$.BeforeInstanceRevision')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.BeforeStableHash')=json_extract(NEW.ManagementSkillJson,'$.BeforeStableHash')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.AfterStableHash')=json_extract(NEW.ManagementSkillJson,'$.AfterStableHash')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Skill.BeforeSources')=json_extract(NEW.ManagementSkillJson,'$.BeforeSources')
    AND json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Skill.AfterSources')=json_extract(NEW.ManagementSkillJson,'$.AfterSources')))
 ),0);
END;
CREATE TRIGGER TR_TenantOperationReceipts_ManagementSkill_UPDATE BEFORE UPDATE ON TenantOperationReceipts
WHEN OLD.ManagementSkillJson IS NOT NEW.ManagementSkillJson
BEGIN SELECT RAISE(ABORT,'Skill effect evidence is immutable'); END;
CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Insert BEFORE INSERT ON TenantOperationReceipts
WHEN NEW.BrowserArtifactsJson IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'Browser Artifact receipt requires actual owned publication') WHERE NOT COALESCE((
NEW.RequestId IS NOT NULL AND length(CAST(NEW.BrowserArtifactsJson AS BLOB)) <= 4096
AND json_valid(NEW.BrowserArtifactsJson) AND json_type(NEW.BrowserArtifactsJson) IS 'object'
AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson)) = 3
AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserArtifactsJson) WHERE key IN ('status','reasonCode','files')) = 3
AND json_extract(NEW.BrowserArtifactsJson, '$.status') IN ('published','empty','unavailable')
AND (json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL OR json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IN
('browser_command_not_completed','artifact_publication_unavailable','permission_denied','artifact_quota_exceeded','capture_limit','download_limit','capture_invalid','download_invalid'))
AND json_type(NEW.BrowserArtifactsJson, '$.files') IS 'array'
AND json_array_length(NEW.BrowserArtifactsJson, '$.files') <= 3
AND ((json_extract(NEW.BrowserArtifactsJson, '$.status') = 'published' AND NEW.Outcome = 0
AND json_array_length(NEW.BrowserArtifactsJson, '$.files') > 0)
OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'empty' AND NEW.Outcome = 0
AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL)
OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'unavailable'
AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NOT NULL))
AND EXISTS (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId = NEW.RequestId
AND json_type(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object')
AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'capture') <= 1
AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'download') <= 2
AND (SELECT count(DISTINCT json_extract(value, '$.artifactId')) FROM json_each(NEW.BrowserArtifactsJson, '$.files')) = json_array_length(NEW.BrowserArtifactsJson, '$.files')
AND NOT EXISTS (SELECT 1 FROM json_each(NEW.BrowserArtifactsJson, '$.files') file
WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 2
OR (SELECT count(DISTINCT key) FROM json_each(file.value) WHERE key IN ('kind','artifactId')) <> 2
OR json_type(file.value, '$.artifactId') IS NOT 'text' OR json_extract(file.value, '$.kind') NOT IN ('capture','download')
OR NOT EXISTS (SELECT 1 FROM Artifacts artifact JOIN TenantApprovalRequests request ON request.RequestId = NEW.RequestId
WHERE artifact.TenantId = NEW.TenantId AND artifact.SessionId = NEW.SessionId AND artifact.AgentRunId = NEW.RunId
AND artifact.ArtifactId = json_extract(file.value, '$.artifactId')
AND ((json_extract(file.value, '$.kind') = 'capture'
AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
AND artifact.ArtifactId = lower(NEW.OperationId) AND artifact.ContentType IN ('image/png','image/jpeg','image/webp')
AND artifact.ByteSize BETWEEN 1 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes'))
OR (json_extract(file.value, '$.kind') = 'download'
AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1
AND artifact.ByteSize BETWEEN 0 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes')))))
),0);
END;
CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Update BEFORE UPDATE ON TenantOperationReceipts
WHEN OLD.BrowserArtifactsJson IS NOT NULL OR NEW.BrowserArtifactsJson IS NOT NULL
BEGIN SELECT RAISE(ABORT, 'Browser Artifact receipts are immutable'); END;
CREATE TRIGGER TR_TenantRuns_Configuration_INSERT BEFORE INSERT ON AgentRuns
WHEN NEW.TenantId IS NOT NULL AND (json_extract(NEW.PayloadJson,'$.admission.activation.kind')=8
 OR json_extract(NEW.PayloadJson,'$.admission.activation.kind')=9 AND
  (json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.EnvironmentKind')='configuration'
   OR EXISTS(SELECT 1 FROM AgentRuns parent WHERE parent.AgentRunId=json_extract(NEW.PayloadJson,'$.admission.activation.sourceAgentRunId')
    AND json_extract(json_extract(parent.PayloadJson,'$.admission.activation.evidenceJson'),'$.EnvironmentKind')='configuration')))
BEGIN
 SELECT RAISE(ABORT,'Configuration continuation requires an exact completed remedy and current causal session') WHERE json_extract(NEW.PayloadJson,'$.admission.activation.kind')=8 AND NOT EXISTS
 (SELECT 1 FROM AgentRuns source JOIN TenantOperationReceipts effect ON effect.RunId=source.AgentRunId
 JOIN TenantApprovalRequests request ON request.RequestId=effect.RequestId
 JOIN AgentInstances owner ON owner.InstanceId=NEW.AgentInstanceId
 JOIN Sessions session ON session.SessionId=NEW.SessionId
 JOIN SessionSnapshots snapshot ON snapshot.SessionId=session.SessionId
 JOIN AgentRuns root ON root.ActivationId=NEW.RootActivationId
 WHERE source.AgentRunId=json_extract(NEW.PayloadJson,'$.admission.activation.sourceAgentRunId')
 AND source.TenantId=NEW.TenantId AND source.AgentInstanceId=NEW.AgentInstanceId AND source.ExecutorPrincipalId=NEW.ExecutorPrincipalId
 AND source.SessionId=NEW.SessionId AND source.RootActivationId=NEW.RootActivationId AND source.OriginalInitiatorPrincipalId=NEW.OriginalInitiatorPrincipalId
 AND source.Status=4 AND NEW.Status=0 AND effect.Outcome=0 AND effect.ReasonCode='management_mutation_completed'
 AND effect.TenantId=NEW.TenantId AND effect.InstanceId=NEW.AgentInstanceId AND effect.SessionId=NEW.SessionId
 AND effect.RootActivationId=NEW.RootActivationId AND effect.ExecutorPrincipalId=NEW.ExecutorPrincipalId
 AND request.TenantId=NEW.TenantId AND request.InstanceId=NEW.AgentInstanceId AND request.ExecutorPrincipalId=NEW.ExecutorPrincipalId
 AND request.Status=1 AND request.OperationId=effect.OperationId AND request.Fingerprint=effect.ActionHash
 AND json_type(request.PayloadJson,'$.Operation.ManagementMutation.Remedy')='object'
 AND effect.InstanceRevision=owner.Revision AND owner.Revision=json_extract(NEW.PayloadJson,'$.admission.configuration.instanceRevision')
 AND json_extract(NEW.PayloadJson,'$.admission.executionContext.origin')=2
 AND json_extract(NEW.PayloadJson,'$.admission.executionContext.parentRunId')=source.AgentRunId
 AND json_extract(NEW.PayloadJson,'$.admission.executionContext.parentActivationId')=source.ActivationId
 AND json_extract(NEW.PayloadJson,'$.admission.activation.sourceSessionId')=source.SessionId
 AND json_extract(NEW.PayloadJson,'$.admission.activation.sourceEventId')=source.ActivationId
 AND json_extract(NEW.PayloadJson,'$.admission.activation.parentActivationId')=source.ActivationId
 AND json_extract(NEW.PayloadJson,'$.admission.activation.dedupeKey')='configuration-continuation:'||source.AgentRunId
 AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.InputBoundarySequence')=snapshot.LastEntrySequence
 AND json_array_length(NEW.PayloadJson,'$.admission.activation.sourceEntryIds')=0 AND json_extract(NEW.PayloadJson,'$.maxAttempts')=1
 AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.EnvironmentKind')='configuration'
 AND json_extract(json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson'),'$.ContinuationTarget')=json_extract(json_extract(request.PayloadJson,'$.Operation.ManagementMutation.Remedy.ArgumentsJson'),'$.target')
 AND session.ArchivedAtUtc IS NULL AND session.DurablyDeletedAtUtc IS NULL AND session.Status IN ('Created','Attached') AND (session.Surfaces & 1)=1 AND snapshot.LifecycleStatus='Active'
 AND root.TenantId=NEW.TenantId AND root.SessionId=NEW.SessionId AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0
 AND EXISTS(SELECT 1 FROM ActivationSourceEntries input JOIN ConversationEntries entry ON entry.EntryId=input.EntryId AND entry.SessionId=input.SessionId WHERE input.ActivationId=root.ActivationId AND entry.Role='User')
 AND NOT EXISTS(SELECT 1 FROM ConversationEntries later WHERE later.SessionId=NEW.SessionId AND later.Role='User'
  AND later.EntrySequence>(SELECT max(entry.EntrySequence) FROM ActivationSourceEntries input JOIN ConversationEntries entry ON entry.EntryId=input.EntryId AND entry.SessionId=input.SessionId WHERE input.ActivationId=root.ActivationId AND entry.Role='User'))
 AND NOT EXISTS(SELECT 1 FROM AgentRuns other WHERE other.SessionId=NEW.SessionId AND other.AgentRunId<>source.AgentRunId
  AND (other.Status NOT IN (4,5,6) OR other.CreatedAtUtc>source.CreatedAtUtc OR (other.RootActivationId=NEW.RootActivationId AND json_extract(other.PayloadJson,'$.admission.activation.kind')=8)))
 );
 SELECT RAISE(ABORT,'Configuration approval continuation must retain its exact target, boundary and pins')
 WHERE json_extract(NEW.PayloadJson,'$.admission.activation.kind')=9 AND NOT EXISTS
 (SELECT 1 FROM AgentRuns parent WHERE parent.AgentRunId=json_extract(NEW.PayloadJson,'$.admission.activation.sourceAgentRunId')
 AND parent.TenantId=NEW.TenantId AND parent.SessionId=NEW.SessionId AND parent.AgentInstanceId=NEW.AgentInstanceId AND parent.ExecutorPrincipalId=NEW.ExecutorPrincipalId
 AND json_extract(parent.PayloadJson,'$.admission.activation.evidenceJson')=json_extract(NEW.PayloadJson,'$.admission.activation.evidenceJson')
 AND json_extract(parent.PayloadJson,'$.admission.configuration')=json_extract(NEW.PayloadJson,'$.admission.configuration'));
END;
CREATE TRIGGER TR_TenantRuns_Configuration_UPDATE BEFORE UPDATE ON AgentRuns
WHEN OLD.TenantId IS NOT NULL AND (json_extract(OLD.PayloadJson,'$.admission.activation.kind')=8
 OR json_extract(json_extract(OLD.PayloadJson,'$.admission.activation.evidenceJson'),'$.EnvironmentKind')='configuration'
 OR json_extract(NEW.PayloadJson,'$.admission.activation.kind')=8)
 AND (NEW.TenantId IS NOT OLD.TenantId OR NEW.AgentInstanceId IS NOT OLD.AgentInstanceId OR NEW.ExecutorPrincipalId IS NOT OLD.ExecutorPrincipalId
 OR NEW.SessionId IS NOT OLD.SessionId OR NEW.ActivationId IS NOT OLD.ActivationId OR NEW.RootActivationId IS NOT OLD.RootActivationId
 OR NEW.OriginalInitiatorPrincipalId IS NOT OLD.OriginalInitiatorPrincipalId OR json_extract(NEW.PayloadJson,'$.admission') IS NOT json_extract(OLD.PayloadJson,'$.admission'))
BEGIN SELECT RAISE(ABORT,'Retain immutable configuration continuation admission'); END;
CREATE TRIGGER TR_TenantSessions_Creator_Insert BEFORE INSERT ON TenantSessions
WHEN NOT EXISTS (SELECT 1 FROM Principals p WHERE p.PrincipalId = NEW.CreatedByPrincipalId AND p.TenantId = NEW.TenantId
AND (p.Kind = 0 OR p.Kind = 1 AND p.PrincipalId = NEW.ExecutorPrincipalId))
BEGIN SELECT RAISE(ABORT, 'same tenant Session creator required'); END;
CREATE TRIGGER TR_TenantSessions_Owner_Update BEFORE UPDATE ON TenantSessions
WHEN OLD.TenantId IS NOT NEW.TenantId OR OLD.SessionId IS NOT NEW.SessionId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId
OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.CreatedByPrincipalId IS NOT NEW.CreatedByPrincipalId OR OLD.CreatedAt IS NOT NEW.CreatedAt
BEGIN SELECT RAISE(ABORT, 'tenant Session owner is immutable'); END;
CREATE TRIGGER TR_TenantUserProfiles_Owner_Update BEFORE UPDATE ON TenantUserProfiles
WHEN OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId
    OR NEW.Revision <> OLD.Revision + 1 OR NEW.UpdatedAtUtc < OLD.UpdatedAtUtc
BEGIN SELECT RAISE(ABORT, 'User Profile ownership and revision are immutable'); END;
CREATE TRIGGER TR_TenantUserProfiles_Revision_Insert BEFORE INSERT ON TenantUserProfiles
WHEN NEW.Revision <> 1
BEGIN SELECT RAISE(ABORT, 'User Profile starts at revision one'); END;
CREATE TRIGGER TR_TenantUserProfiles_Shape_Insert BEFORE INSERT ON TenantUserProfiles BEGIN SELECT RAISE(ABORT, 'Invalid owned User Profile') WHERE NOT COALESCE((json_valid(NEW.PreferencesJson) AND json_type(NEW.PreferencesJson) = 'object'
AND length(CAST(NEW.PreferencesJson AS BLOB)) <= 4096
AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) <= 4
AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) = (SELECT count(DISTINCT key) FROM json_each(NEW.PreferencesJson))
AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PreferencesJson) p WHERE
    p.key NOT IN ('preferredName','language','locale','timeZone') OR p.type <> 'object'
    OR (SELECT count(*) FROM json_each(p.value)) <> 3
    OR (SELECT count(DISTINCT key) FROM json_each(p.value) WHERE key IN ('value','source','updatedAt')) <> 3
    OR json_type(p.value, '$.value') IS NOT 'text' OR length(trim(json_extract(p.value, '$.value'))) = 0
    OR length(json_extract(p.value, '$.value')) > CASE p.key WHEN 'preferredName' THEN 128 WHEN 'language' THEN 32 WHEN 'locale' THEN 35 ELSE 64 END
    OR json_type(p.value, '$.source') IS NOT 'integer' OR json_extract(p.value, '$.source') <> 0
    OR json_type(p.value, '$.updatedAt') IS NOT 'text' OR julianday(json_extract(p.value, '$.updatedAt')) IS NULL
    OR NOT (substr(json_extract(p.value, '$.updatedAt'), -1) = 'Z' OR substr(json_extract(p.value, '$.updatedAt'), -6) = '+00:00')
    OR julianday(json_extract(p.value, '$.updatedAt')) > julianday(NEW.UpdatedAtUtc / 1000.0, 'unixepoch'))), 0); END;
CREATE TRIGGER TR_TenantUserProfiles_Shape_Update BEFORE UPDATE ON TenantUserProfiles BEGIN SELECT RAISE(ABORT, 'Invalid owned User Profile') WHERE NOT COALESCE((json_valid(NEW.PreferencesJson) AND json_type(NEW.PreferencesJson) = 'object'
AND length(CAST(NEW.PreferencesJson AS BLOB)) <= 4096
AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) <= 4
AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) = (SELECT count(DISTINCT key) FROM json_each(NEW.PreferencesJson))
AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PreferencesJson) p WHERE
    p.key NOT IN ('preferredName','language','locale','timeZone') OR p.type <> 'object'
    OR (SELECT count(*) FROM json_each(p.value)) <> 3
    OR (SELECT count(DISTINCT key) FROM json_each(p.value) WHERE key IN ('value','source','updatedAt')) <> 3
    OR json_type(p.value, '$.value') IS NOT 'text' OR length(trim(json_extract(p.value, '$.value'))) = 0
    OR length(json_extract(p.value, '$.value')) > CASE p.key WHEN 'preferredName' THEN 128 WHEN 'language' THEN 32 WHEN 'locale' THEN 35 ELSE 64 END
    OR json_type(p.value, '$.source') IS NOT 'integer' OR json_extract(p.value, '$.source') <> 0
    OR json_type(p.value, '$.updatedAt') IS NOT 'text' OR julianday(json_extract(p.value, '$.updatedAt')) IS NULL
    OR NOT (substr(json_extract(p.value, '$.updatedAt'), -1) = 'Z' OR substr(json_extract(p.value, '$.updatedAt'), -6) = '+00:00')
    OR julianday(json_extract(p.value, '$.updatedAt')) > julianday(NEW.UpdatedAtUtc / 1000.0, 'unixepoch'))), 0); END;
CREATE TRIGGER TR_TenantUsers_LearnedMemoryOwner_Update BEFORE UPDATE ON TenantUsers
WHEN (OLD.UserId IS NOT NEW.UserId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId)
    AND EXISTS (SELECT 1 FROM TenantLearnedMemories m WHERE m.UserId = OLD.UserId)
BEGIN SELECT RAISE(ABORT, 'Retain the actual learned-memory User owner'); END;
CREATE TRIGGER TR_TenantUsers_ProfileOwner_Update BEFORE UPDATE ON TenantUsers
WHEN (OLD.UserId IS NOT NEW.UserId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId)
    AND EXISTS (SELECT 1 FROM TenantUserProfiles p WHERE p.TenantId = OLD.TenantId AND p.PrincipalId = OLD.PrincipalId AND p.AccountId = OLD.AccountId)
BEGIN SELECT RAISE(ABORT, 'Retain the actual User Profile membership owner'); END;
CREATE TRIGGER TR_TenantWebhook_Delivery_Delete BEFORE DELETE ON ExternalEventDeliveries
WHEN OLD.TenantId IS NOT NULL
BEGIN
SELECT RAISE(ABORT, 'tenant event facts and coverage are retained');
END;
CREATE TRIGGER TR_TenantWebhook_Delivery_Insert BEFORE INSERT ON ExternalEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 6 AND (NEW.Status NOT IN (1,5,6) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (1,6) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 1 AND ((NEW.Status = 6 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 1 AND o.Disposition = 6) OR (NEW.Status = 2 AND o.Disposition = 4)))))),0);
END;
CREATE TRIGGER TR_TenantWebhook_Delivery_Update BEFORE UPDATE ON ExternalEventDeliveries
BEGIN
SELECT RAISE(ABORT, 'tenant delivery source is immutable') WHERE (OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL) AND (OLD.TenantId IS NOT NEW.TenantId OR OLD.ExecutorPrincipalId IS NOT NEW.ExecutorPrincipalId OR OLD.ProfileId IS NOT NEW.ProfileId OR OLD.AgentInstanceId IS NOT NEW.AgentInstanceId OR OLD.AutomationId IS NOT NEW.AutomationId OR OLD.TriggerId IS NOT NEW.TriggerId OR OLD.EventId IS NOT NEW.EventId OR OLD.SnapshotJson IS NOT NEW.SnapshotJson);
SELECT RAISE(ABORT, 'terminal delivery cannot be reopened') WHERE OLD.TenantId IS NOT NULL AND (OLD.ReservedOccurrenceId IS NOT NULL AND OLD.ReservedOccurrenceId IS NOT NEW.ReservedOccurrenceId OR OLD.Status IN (1,2,3,5) AND (OLD.Status IS NOT NEW.Status OR OLD.DecisionJson IS NOT NEW.DecisionJson OR OLD.Code IS NOT NEW.Code));
SELECT RAISE(ABORT, 'delivery requires its exact frozen receipt and occurrence') WHERE NOT COALESCE(((NEW.TenantId IS NULL AND NEW.ExecutorPrincipalId IS NULL AND NEW.ReservedOccurrenceId IS NULL AND NOT EXISTS (SELECT 1 FROM SystemIdentities i WHERE i.Kind = 0 AND i.AgentInstanceId = NEW.AgentInstanceId AND i.TenantId IS NOT NULL) AND NOT EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = NEW.EventId AND e.TenantId IS NOT NULL) AND (NEW.SnapshotJson IS NULL OR NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson, '$') WHERE lower(key) IN ('tenantid','executorprincipalid')) AND NOT EXISTS (SELECT 1 FROM json_tree(NEW.SnapshotJson) field JOIN SystemIdentities i ON i.AgentInstanceId = field.value WHERE lower(field.key) = 'agentinstanceid' AND i.Kind = 0 AND i.TenantId IS NOT NULL))) OR (NEW.TenantId IS NOT NULL AND (json_type(NEW.SnapshotJson, '$') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(NEW.SnapshotJson, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(NEW.SnapshotJson, '$.Owner.AgentInstanceId') IS NEW.AgentInstanceId AND lower(json_extract(NEW.SnapshotJson, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(NEW.SnapshotJson, '$.Owner.ExecutorPrincipalId')) IS lower(NEW.ExecutorPrincipalId) AND json_type(NEW.SnapshotJson, '$.Owner.profileId') IS 'null' AND (json_type(NEW.SnapshotJson, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(NEW.SnapshotJson, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(NEW.SnapshotJson, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(NEW.SnapshotJson, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(NEW.SnapshotJson, '$.TriggerRevision') >= 1 AND (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'null' OR (json_type(NEW.SnapshotJson, '$.FilterExpression') IS 'text' AND length(json_extract(NEW.SnapshotJson, '$.FilterExpression')) <= 1024)) AND ((json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 0 AND json_type(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(NEW.SnapshotJson, '$.Dispatch.Mode') = 1 AND json_extract(NEW.SnapshotJson, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(NEW.SnapshotJson, '$.AutomationId') IS NEW.AutomationId AND json_extract(NEW.SnapshotJson, '$.TriggerId') IS NEW.TriggerId AND json_extract(NEW.SnapshotJson, '$.SourceKind') = 1 AND EXISTS (SELECT 1 FROM ExternalEvents e, json_each(e.SubscriptionsJson) f WHERE e.EventId = NEW.EventId AND e.TenantId = NEW.TenantId AND json(f.value) IS json(NEW.SnapshotJson)) AND NEW.Status BETWEEN 0 AND 6 AND (NEW.Status NOT IN (1,5,6) OR json_extract(NEW.DecisionJson, '$.Matched') = 1) AND (NEW.ReservedOccurrenceId IS NULL AND NEW.Status NOT IN (1,6) OR NEW.ReservedOccurrenceId IS NOT NULL AND EXISTS (SELECT 1 FROM TriggerOccurrences o WHERE o.OccurrenceId = NEW.ReservedOccurrenceId AND o.TenantId = NEW.TenantId AND o.AgentInstanceId = NEW.AgentInstanceId AND o.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND o.AutomationId = NEW.AutomationId AND o.TriggerId = NEW.TriggerId AND o.SourceEventId = NEW.EventId AND o.SourceKind = 1 AND ((NEW.Status = 6 AND o.Disposition = 3 AND o.DispositionReason = 'admission_approval_pending') OR (NEW.Status = 1 AND o.Disposition = 6) OR (NEW.Status = 2 AND o.Disposition = 4)))))),0);
END;
CREATE TRIGGER TR_TenantWebhook_Receipt_Delete BEFORE DELETE ON ExternalEvents
        WHEN OLD.TenantId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'tenant webhook receipts are retained'); END;
CREATE TRIGGER TR_TenantWebhook_Receipt_Insert BEFORE INSERT ON ExternalEvents
        WHEN NEW.TenantId IS NOT NULL OR EXISTS (SELECT 1 FROM WebhookEvents s WHERE s.ResourceId = NEW.ResourceId AND s.TenantId IS NOT NULL)
        BEGIN
            SELECT RAISE(ABORT, 'tenant receipt requires its exact active source and bounded evidence') WHERE NOT COALESCE((
                EXISTS (SELECT 1 FROM WebhookEvents s JOIN Tenants t ON t.TenantId = s.TenantId
                    WHERE s.ResourceId = NEW.ResourceId AND s.TenantId = NEW.TenantId AND s.Revision = NEW.SourceRevision AND s.Status = 0 AND t.Status = 0)
                AND NEW.SourceEventId NOT GLOB '*[^!-~]*'
                AND (SELECT count(*) FROM json_each(NEW.EvidenceJson)) = 5
                AND (SELECT count(DISTINCT key) FROM json_each(NEW.EvidenceJson) WHERE key IN ('sourceEventId','data','occurredAtUtc','rootAgentRunId','triggerDepth')) = 5
                AND json_type(NEW.EvidenceJson, '$.rootAgentRunId') IS 'null'
                AND json_type(NEW.EvidenceJson, '$.triggerDepth') IS 'integer'
                AND (json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'null' OR json_type(NEW.EvidenceJson, '$.occurredAtUtc') IS 'text')),0);
SELECT RAISE(ABORT, 'Webhook receipt requires its exact frozen subscriptions') WHERE NOT COALESCE((json_type(NEW.SubscriptionsJson) IS 'array' AND json_array_length(NEW.SubscriptionsJson) <= 1024 AND json_array_length(NEW.SubscriptionsJson) = (SELECT count(*) FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.AdmittedAtUtc) AND t.Enabled = 1 AND t.Kind = 1 AND t.EventId = NEW.ResourceId) AND (SELECT count(DISTINCT json_extract(value, '$.TriggerId')) FROM json_each(NEW.SubscriptionsJson)) = json_array_length(NEW.SubscriptionsJson) AND NOT EXISTS (SELECT 1 FROM json_each(NEW.SubscriptionsJson) f WHERE NOT COALESCE(((json_type(f.value, '$') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$')) = 10 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$') WHERE key IN ('AutomationId','Owner','TriggerRevision','FilterExpression','Dispatch','SourceKind','ResourceId','TriggerId','ExpressionVersion','FilterHash')) = 10) AND (json_type(f.value, '$.Owner') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Owner')) = 4 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Owner') WHERE key IN ('AgentInstanceId','profileId','TenantId','ExecutorPrincipalId')) = 4) AND json_extract(f.value, '$.Owner.AgentInstanceId') IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(json_extract(f.value, '$.Owner.TenantId')) IS lower(NEW.TenantId) AND lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) IS lower(json_extract(f.value, '$.Owner.ExecutorPrincipalId')) AND json_type(f.value, '$.Owner.profileId') IS 'null' AND (json_type(f.value, '$.Dispatch') IS 'object' AND (SELECT count(*) FROM json_each(f.value, '$.Dispatch')) = 2 AND (SELECT count(DISTINCT key) FROM json_each(f.value, '$.Dispatch') WHERE key IN ('Mode','WindowSeconds')) = 2) AND json_extract(f.value, '$.ExpressionVersion') IS 'js-expression-v1' AND json_extract(f.value, '$.TriggerRevision') >= 1 AND (json_type(f.value, '$.FilterExpression') IS 'null' OR (json_type(f.value, '$.FilterExpression') IS 'text' AND length(json_extract(f.value, '$.FilterExpression')) <= 1024)) AND ((json_extract(f.value, '$.Dispatch.Mode') = 0 AND json_type(f.value, '$.Dispatch.WindowSeconds') IS 'null') OR (json_extract(f.value, '$.Dispatch.Mode') = 1 AND json_extract(f.value, '$.Dispatch.WindowSeconds') BETWEEN 60 AND 3600)) AND json_extract(f.value, '$.SourceKind') = 1 AND json_extract(f.value, '$.ResourceId') IS NEW.ResourceId AND EXISTS (SELECT 1 FROM Automations a JOIN AutomationTriggers t ON t.AutomationId = a.AutomationId WHERE a.TenantId = NEW.TenantId AND a.Status = 0 AND (a.ExpiresAtUtc IS NULL OR a.ExpiresAtUtc > NEW.AdmittedAtUtc) AND t.Enabled = 1 AND t.Kind = 1 AND t.EventId = NEW.ResourceId AND a.AutomationId IS json_extract(f.value, '$.AutomationId') AND a.AgentInstanceId IS json_extract(f.value, '$.Owner.AgentInstanceId') AND lower(a.ExecutorPrincipalId) IS json_extract(f.value, '$.Owner.ExecutorPrincipalId') AND t.TriggerId IS json_extract(f.value, '$.TriggerId') AND t.Revision = json_extract(f.value, '$.TriggerRevision') AND t.FilterExpression IS json_extract(f.value, '$.FilterExpression') AND t.DispatchMode = json_extract(f.value, '$.Dispatch.Mode') AND t.DispatchWindowSeconds IS json_extract(f.value, '$.Dispatch.WindowSeconds'))),0))),0);
        END;
CREATE TRIGGER TR_TenantWebhook_Receipt_Update BEFORE UPDATE ON ExternalEvents
        WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'tenant webhook receipts are immutable'); END;
CREATE TRIGGER TR_TenantWebhook_Recipients_Legacy BEFORE INSERT ON ExternalEvents
WHEN NEW.TenantId IS NULL
BEGIN
SELECT RAISE(ABORT, 'legacy receipts cannot carry tenant subscriptions') WHERE NOT COALESCE((NEW.SubscriptionsJson IS NULL),0);
END;
CREATE TRIGGER TR_TenantWebhook_Source_Delete BEFORE DELETE ON WebhookEvents
        WHEN OLD.TenantId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'tenant webhook sources are retained; revoke instead'); END;
CREATE TRIGGER TR_TenantWebhook_Source_Insert BEFORE INSERT ON WebhookEvents
        WHEN NEW.TenantId IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT, 'tenant webhook requires its actual human creator') WHERE NOT (
                NEW.Revision = 1 AND NEW.CreatedAtUtc = NEW.UpdatedAtUtc AND EXISTS (
                    SELECT 1 FROM TenantUsers u JOIN Principals p ON p.PrincipalId = u.PrincipalId AND p.TenantId = u.TenantId
                    JOIN Tenants t ON t.TenantId = u.TenantId
                    WHERE u.TenantId = NEW.TenantId AND u.PrincipalId = NEW.CreatedByPrincipalId AND u.Status = 0 AND p.Status = 0 AND p.Kind = 0 AND t.Status = 0));
        END;
CREATE TRIGGER TR_TenantWebhook_Source_Update BEFORE UPDATE ON WebhookEvents
        WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
        BEGIN SELECT RAISE(ABORT, 'tenant webhook identity is immutable and requires exact revision') WHERE
            OLD.TenantId IS NOT NEW.TenantId OR OLD.CreatedByPrincipalId IS NOT NEW.CreatedByPrincipalId OR OLD.ResourceId IS NOT NEW.ResourceId
            OR OLD.EventKey IS NOT NEW.EventKey OR OLD.Kind IS NOT NEW.Kind OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
            OR NEW.Revision <> OLD.Revision + 1 OR NEW.UpdatedAtUtc < OLD.UpdatedAtUtc;
        END;