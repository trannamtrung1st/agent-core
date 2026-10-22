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
