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
