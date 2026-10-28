-- Frozen predecessor guards plus the incumbent source-retention exception. No table/data rebuild.
DROP TRIGGER WorkflowExecutionImmutable;
CREATE TRIGGER WorkflowExecutionImmutable BEFORE UPDATE ON TenantWorkflowExecutions BEGIN
 SELECT RAISE(ABORT,'Workflow business source is immutable and terminal state is monotonic') WHERE NOT COALESCE((
   NEW.TenantId=OLD.TenantId AND NEW.ExecutionId=OLD.ExecutionId AND NEW.WorkflowId=OLD.WorkflowId AND NEW.Version=OLD.Version
   AND NEW.SourceScope=OLD.SourceScope AND NEW.RequestKey=OLD.RequestKey AND NEW.CreatedAt=OLD.CreatedAt
   AND NEW.Revision=OLD.Revision+1 AND OLD.Status<8 AND NEW.Status BETWEEN 0 AND 11
   AND json_extract(NEW.Json,'$.Revision')=NEW.Revision AND json_extract(NEW.Json,'$.Status')=NEW.Status
   AND (OLD.Status<8 AND (NEW.Status<8 AND json_extract(NEW.Json,'$.TerminalAt') IS NULL OR NEW.Status>=8 AND json_type(NEW.Json,'$.TerminalAt')='text'))
   AND json_remove(NEW.Json,'$.Revision','$.Status','$.CancellationRequested','$.Cancellation','$.ReasonCode','$.TerminalAt','$.SourceClosure')
     IS json_remove(OLD.Json,'$.Revision','$.Status','$.CancellationRequested','$.Cancellation','$.ReasonCode','$.TerminalAt','$.SourceClosure')
   AND (json_extract(OLD.Json,'$.CancellationRequested')=0 OR json_extract(NEW.Json,'$.CancellationRequested')=1)
   AND (json_extract(OLD.Json,'$.Cancellation') IS NULL OR json_extract(NEW.Json,'$.Cancellation') IS json_extract(OLD.Json,'$.Cancellation'))
   AND (json_extract(OLD.Json,'$.SourceClosure') IS NULL OR json_extract(NEW.Json,'$.SourceClosure') IS json_extract(OLD.Json,'$.SourceClosure'))
   AND (NEW.Status<>8 OR EXISTS(SELECT 1 FROM TenantWorkflowSteps step WHERE step.TenantId=NEW.TenantId AND step.ExecutionId=NEW.ExecutionId
     AND json_extract(step.Json,'$.Kind')=5 AND json_extract(step.Json,'$.Status')=3 AND json_extract(step.Json,'$.ProofAccepted')=1))
   AND (NEW.Status<>10 OR json_extract(NEW.Json,'$.CancellationRequested')=1)) OR (NEW.TenantId=OLD.TenantId AND NEW.ExecutionId=OLD.ExecutionId AND NEW.WorkflowId=OLD.WorkflowId AND NEW.Version=OLD.Version
 AND NEW.SourceScope=OLD.SourceScope AND NEW.RequestKey=OLD.RequestKey AND NEW.CreatedAt=OLD.CreatedAt
 AND NEW.Status=OLD.Status AND OLD.Status>=8 AND NEW.Revision=OLD.Revision+1
 AND json_extract(NEW.Json,'$.Revision')=NEW.Revision
 AND json_extract(OLD.Json,'$.ContentRetiredAt') IS NULL AND json_type(NEW.Json,'$.ContentRetiredAt')='text'
 AND json_extract(NEW.Json,'$.InputsJson')='{}'
 AND json_remove(NEW.Json,'$.Revision','$.InputsJson','$.ContentRetiredAt') IS json_remove(OLD.Json,'$.Revision','$.InputsJson','$.ContentRetiredAt')
 AND EXISTS(SELECT 1 FROM Sessions s WHERE lower(s.SessionId)=lower(json_extract(OLD.Json,'$.Source.SessionId')) AND s.DurablyDeletedAtUtc IS NOT NULL)
 AND NOT EXISTS(SELECT 1 FROM TenantDelegatedTasks t WHERE t.TenantId=OLD.TenantId AND t.WorkflowExecutionId=OLD.ExecutionId AND t.State<4)
 AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId
   AND (lower(r.SessionId)=lower(json_extract(OLD.Json,'$.Source.SessionId')) OR EXISTS(SELECT 1 FROM TenantDelegatedTasks t
       WHERE t.TenantId=OLD.TenantId AND t.WorkflowExecutionId=OLD.ExecutionId AND lower(t.RootActivationId)=lower(r.RootActivationId)))
   AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))),0);
END;

DROP TRIGGER WorkflowExecutionSourceGuard;
CREATE TRIGGER WorkflowExecutionSourceGuard BEFORE INSERT ON TenantWorkflowExecutions BEGIN
 SELECT RAISE(ABORT,'Workflow requires its genuine human command or currently claimed Run') WHERE NOT COALESCE((
   NEW.Revision=1 AND NEW.Status=0 AND json_valid(NEW.Json) AND json_extract(NEW.Json,'$.ContentRetiredAt') IS NULL AND json_extract(NEW.Json,'$.SourceClosure') IS NULL
   AND lower(json_extract(NEW.Json,'$.TenantId'))=lower(NEW.TenantId)
   AND lower(json_extract(NEW.Json,'$.ExecutionId'))=lower(NEW.ExecutionId)
   AND lower(json_extract(NEW.Json,'$.WorkflowId'))=lower(NEW.WorkflowId)
   AND json_extract(NEW.Json,'$.Version')=NEW.Version AND json_extract(NEW.Json,'$.Revision')=NEW.Revision
   AND json_extract(NEW.Json,'$.Status')=NEW.Status AND json_extract(NEW.Json,'$.RequestKey')=NEW.RequestKey
   AND json_extract(NEW.Json,'$.Source.RequestScope')=NEW.SourceScope
   AND json_extract(NEW.Json,'$.CancellationRequested')=0 AND json_extract(NEW.Json,'$.TerminalAt') IS NULL
   AND EXISTS(SELECT 1 FROM TenantWorkflowVersions version WHERE version.TenantId=NEW.TenantId AND version.WorkflowId=NEW.WorkflowId AND version.Version=NEW.Version
     AND json_extract(version.Json,'$.Graph.Hash')=json_extract(NEW.Json,'$.GraphHash')
     AND EXISTS(SELECT 1 FROM json_each(json_extract(version.Json,'$.Graph.CanonicalJson'),'$.allowedOrigins') origin WHERE origin.value=json_extract(NEW.Json,'$.Source.Kind')))
   AND ((json_extract(NEW.Json,'$.Source.Kind')=0
     AND lower(json_extract(NEW.Json,'$.Source.SourceId'))=lower(NEW.ExecutionId)
     AND json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId') IS json_extract(NEW.Json,'$.Source.PrincipalId')
     AND json_extract(NEW.Json,'$.Source.RunId') IS NULL AND json_extract(NEW.Json,'$.Source.InvokingInstanceId') IS NULL
     AND json_extract(NEW.Json,'$.Source.AutomationId') IS NULL AND json_extract(NEW.Json,'$.Source.RootActivationId') IS NULL
     AND json_extract(NEW.Json,'$.Source.Causation') IS NULL AND json_extract(NEW.Json,'$.SourceBudget') IS NULL
     AND EXISTS(SELECT 1 FROM TenantUsers user JOIN Accounts account ON account.AccountId=user.AccountId
       WHERE user.TenantId=NEW.TenantId AND lower(user.PrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
         AND lower(user.AccountId)=lower(json_extract(NEW.Json,'$.Source.AccountId')) AND user.Status=0 AND account.Status=0))
   OR (json_extract(NEW.Json,'$.Source.Kind')=1 AND json_extract(NEW.Json,'$.Source.AccountId') IS NULL
     AND json_extract(NEW.Json,'$.Source.AutomationId') IS NULL
     AND EXISTS(SELECT 1 FROM AgentRuns source WHERE source.TenantId=NEW.TenantId
       AND source.AgentRunId=json_extract(NEW.Json,'$.Source.RunId') AND source.AgentRunId=json_extract(NEW.Json,'$.Source.SourceId')
       AND source.SessionId=json_extract(NEW.Json,'$.Source.SessionId') AND source.AgentInstanceId=json_extract(NEW.Json,'$.Source.InvokingInstanceId')
       AND lower(source.ExecutorPrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
       AND lower(source.OriginalInitiatorPrincipalId) IS json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId')
       AND source.RootActivationId=json_extract(NEW.Json,'$.Source.RootActivationId')
       AND source.Status=1 AND source.LeaseExpiresAtUtc>NEW.CreatedAt AND json_type(source.PayloadJson,'$.claim.generation')='text'
       AND json_extract(source.PayloadJson,'$.cancellationRequested')=0))
   OR (json_extract(NEW.Json,'$.Source.Kind')=2 AND json_extract(NEW.Json,'$.Source.AccountId') IS NULL
     AND json_extract(NEW.Json,'$.Source.RunId') IS NULL AND json_extract(NEW.Json,'$.Source.SessionId') IS NULL
     AND json_extract(NEW.Json,'$.Source.RootActivationId') IS NULL AND NEW.RequestKey='automation-occurrence'
     AND EXISTS(SELECT 1 FROM TriggerOccurrences occurrence WHERE occurrence.TenantId=NEW.TenantId
       AND lower(occurrence.OccurrenceId)=lower(json_extract(NEW.Json,'$.Source.SourceId'))
       AND lower(occurrence.AutomationId)=lower(json_extract(NEW.Json,'$.Source.AutomationId'))
       AND lower(occurrence.AgentInstanceId)=lower(json_extract(NEW.Json,'$.Source.InvokingInstanceId'))
       AND lower(occurrence.ExecutorPrincipalId)=lower(json_extract(NEW.Json,'$.Source.PrincipalId'))
       AND lower(occurrence.WorkflowInitiatorPrincipalId) IS json_extract(NEW.Json,'$.Source.OriginalHumanPrincipalId')
       AND occurrence.ExecutionTargetKind=2 AND occurrence.Disposition=0 AND occurrence.RoutingRevision=0
       AND occurrence.AcceptedWorkflowExecutionId IS NULL AND occurrence.AcceptedAgentRunId IS NULL AND occurrence.ExecutionSessionId IS NULL
       AND lower(json_extract(occurrence.WorkflowTargetJson,'$.WorkflowId'))=lower(NEW.WorkflowId)
       AND json_extract(occurrence.WorkflowTargetJson,'$.Version')=NEW.Version
       AND json_extract(occurrence.WorkflowTargetJson,'$.GraphHash')=json_extract(NEW.Json,'$.GraphHash')
       AND json_extract(occurrence.WorkflowTargetJson,'$.InputsJson')=json_extract(NEW.Json,'$.InputsJson'))))) ,0);
END;

DROP TRIGGER TaskAssignmentImmutable;
CREATE TRIGGER TaskAssignmentImmutable BEFORE UPDATE ON TenantDelegatedTasks WHEN (
    NEW.TenantId<>OLD.TenantId OR NEW.TaskId<>OLD.TaskId OR NEW.SourceSessionId IS NOT OLD.SourceSessionId OR NEW.WorkflowExecutionId IS NOT OLD.WorkflowExecutionId OR NEW.OperationScope<>OLD.OperationScope
    OR NEW.RequestKey<>OLD.RequestKey OR NEW.RequestHash<>OLD.RequestHash OR NEW.WorkerInstanceId<>OLD.WorkerInstanceId
    OR NEW.RootActivationId<>OLD.RootActivationId OR NEW.ParentTaskId IS NOT OLD.ParentTaskId OR NEW.Revision<>OLD.Revision+1
    OR OLD.State>=4 OR (OLD.WorkerSessionId IS NOT NULL AND NEW.WorkerSessionId IS NOT OLD.WorkerSessionId)
    OR json_extract(NEW.Json,'$.Assignment')<>json_extract(OLD.Json,'$.Assignment')
    OR json_extract(NEW.Json,'$.Source')<>json_extract(OLD.Json,'$.Source') OR json_extract(NEW.Json,'$.Request')<>json_extract(OLD.Json,'$.Request')
    ) AND NOT COALESCE((
        OLD.State>=4 AND NEW.State=OLD.State AND NEW.TenantId=OLD.TenantId AND NEW.TaskId=OLD.TaskId
        AND NEW.SourceSessionId IS OLD.SourceSessionId AND NEW.WorkflowExecutionId IS OLD.WorkflowExecutionId AND NEW.OperationScope=OLD.OperationScope AND NEW.RequestKey=OLD.RequestKey
        AND NEW.RequestHash=OLD.RequestHash AND NEW.WorkerInstanceId=OLD.WorkerInstanceId AND NEW.RootActivationId=OLD.RootActivationId
        AND NEW.ParentTaskId IS OLD.ParentTaskId AND NEW.WorkerSessionId IS OLD.WorkerSessionId AND NEW.UpdatedAt=OLD.UpdatedAt AND NEW.Revision=OLD.Revision+1
        AND json_extract(OLD.Json,'$.ContentRetiredAt') IS NULL AND json_type(NEW.Json,'$.ContentRetiredAt')='text'
        AND json_extract(NEW.Json,'$.Request.Objective')='[retired]' AND json_extract(NEW.Json,'$.Request.Title')='[retired]'
        AND json_extract(NEW.Json,'$.Request.RetiredFingerprint')=OLD.RequestHash
        AND json_extract(NEW.Json,'$.Result') IS CASE WHEN json_extract(OLD.Json,'$.Result') IS NULL THEN NULL ELSE '[retired]' END
        AND json_remove(NEW.Json,'$.Revision','$.Request','$.Result','$.ContentRetiredAt')=json_remove(OLD.Json,'$.Revision','$.Request','$.Result','$.ContentRetiredAt')
        AND json_remove(json_extract(NEW.Json,'$.Request'),'$.Objective','$.Title','$.RetiredFingerprint')=json_remove(json_extract(OLD.Json,'$.Request'),'$.Objective','$.Title','$.RetiredFingerprint')
        AND EXISTS(SELECT 1 FROM Sessions s WHERE lower(s.SessionId)=lower(coalesce(OLD.SourceSessionId,json_extract(OLD.Json,'$.Source.Workflow.OriginalSessionId'),CASE WHEN OLD.WorkflowExecutionId IS NOT NULL THEN OLD.WorkerSessionId END)) AND s.DurablyDeletedAtUtc IS NOT NULL)
        AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId AND (lower(r.SessionId)=lower(coalesce(OLD.SourceSessionId,json_extract(OLD.Json,'$.Source.Workflow.OriginalSessionId'),CASE WHEN OLD.WorkflowExecutionId IS NOT NULL THEN OLD.WorkerSessionId END)) OR lower(r.RootActivationId)=lower(OLD.RootActivationId)) AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
    ),0) BEGIN SELECT RAISE(ABORT,'Task assignment and terminal facts are immutable'); END;
