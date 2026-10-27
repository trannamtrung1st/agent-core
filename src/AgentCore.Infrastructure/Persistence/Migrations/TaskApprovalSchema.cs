using System.Text.RegularExpressions;

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Extend the frozen approval table without weakening native evaluation or ordinary operation guards.
internal static class TaskApprovalSchema
{
    internal static string Values => "(ContentRetiredAt IS NULL AND json_type(PayloadJson,'$.Operation.BackgroundStart') IS NOT 'object' AND json_type(PayloadJson,'$.Operation.CompletionRead') IS NOT 'object' AND (((" + EvaluationSandboxApprovalSchema.Values + ") AND json_type(PayloadJson,'$.Operation.TaskDelegation') IS NOT 'object') OR COALESCE((" + TaskValues + "),0) OR COALESCE((" + HumanValues + "),0))) OR COALESCE((ContentRetiredAt IS NOT NULL AND Permission='tasks.delegate' AND Status<>0 AND json_valid(PayloadJson) AND json_extract(PayloadJson,'$.contentRetiredAt')=ContentRetiredAt AND json_extract(PayloadJson,'$.actionHash')=Fingerprint),0)";
    private const string TaskValues = """
        Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt AND ExpiresAt-CreatedAt<=3600000
        AND length(Fingerprint)=64 AND Permission='tasks.delegate' AND ResourceType='session' AND ResourceId=SessionId
        AND RunId IS NOT NULL AND SessionId IS NOT NULL AND AutomationId IS NULL AND CredentialId IS NULL
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(CAST(PayloadJson AS BLOB))<=131072
        AND json_type(PayloadJson,'$.Operation.TaskDelegation')='object'
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=0
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.TenantId')=lower(TenantId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.InstanceId')=lower(InstanceId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.PrincipalId')=lower(ExecutorPrincipalId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.RunId')=RunId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId')=SessionId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.RootActivationId')=RootActivationId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.OriginalInitiatorPrincipalId') IS lower(OriginalInitiatorPrincipalId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.HumanCommandId') IS NULL
        AND length(json_extract(PayloadJson,'$.Operation.TaskDelegation.SourceGeneration'))=36
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Revision')=1
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.State') IN (0,1)
        AND json_type(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.Model')='object'
        AND json_type(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.Budget')='object'
        """;

    private const string HumanValues = """
        Kind=0 AND Status IN (0,1,2,3,4) AND Revision>=1 AND ExpiresAt>CreatedAt AND ExpiresAt-CreatedAt<=3600000
        AND length(Fingerprint)=64 AND Permission='tasks.delegate' AND ResourceType='session'
        AND RunId IS NULL AND SessionId IS NULL AND AutomationId IS NULL AND CredentialId IS NULL
        AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(CAST(PayloadJson AS BLOB))<=131072
        AND json_type(PayloadJson,'$.Operation.TaskDelegation')='object'
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=1
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.TenantId')=lower(TenantId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.InstanceId')=lower(InstanceId)
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.PrincipalId')=lower(ExecutorPrincipalId)
        AND ExecutorPrincipalId=OriginalInitiatorPrincipalId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId')=ResourceId
        AND json_extract(PayloadJson,'$.SessionId')=ResourceId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.RootActivationId')=RootActivationId
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.RunId') IS NULL
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActivationId') IS NULL
        AND length(json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.HumanCommandId'))=36
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.SourceGeneration') IS NULL
        AND json_extract(PayloadJson,'$.Operation.SpaceContext') IS NULL
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Revision')=1
        AND json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.State') IN (0,1)
        AND json_extract(PayloadJson,'$.RunId')=COALESCE(json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.InitialWorkerRunId'),json_extract(PayloadJson,'$.Operation.TaskDelegation.ReservedWorkerRunId'))
        AND length(json_extract(PayloadJson,'$.RunId'))=36
        AND json_type(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.Model')='object'
        AND json_type(PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.Budget')='object'
        """;

    internal static string Rebuild(bool tasks = true)
    {
        var predecessor = EvaluationSandboxApprovalSchema.Rebuild();
        if (!predecessor.Contains(EvaluationSandboxApprovalSchema.Values, StringComparison.Ordinal))
            throw new InvalidOperationException("Frozen Task approval predecessor differs.");
        const string columns = "RequestId,TenantId,RunId,SessionId,AutomationId,InstanceId,RootActivationId,ExecutorPrincipalId,OriginalInitiatorPrincipalId,OperationId,Fingerprint,Permission,ResourceType,ResourceId,CredentialId,SandboxProfileId,SandboxProfileRevision,Kind,Status,Revision,CreatedAt,ExpiresAt,PayloadJson";
        if (!tasks) return predecessor.Replace("INSERT INTO evaluation_approval_rebuild SELECT * FROM TenantApprovalRequests;",
            "INSERT INTO evaluation_approval_rebuild SELECT " + columns + " FROM TenantApprovalRequests;", StringComparison.Ordinal);
        var sql = predecessor.Replace(EvaluationSandboxApprovalSchema.Values, Values, StringComparison.Ordinal)
            .Replace("evaluation_approval_rebuild", "task_approval_rebuild", StringComparison.Ordinal);
        const string payloadColumn = "\"PayloadJson\" TEXT NOT NULL";
        if (Regex.Matches(sql, Regex.Escape(payloadColumn)).Count != 1) throw new InvalidOperationException("Frozen approval payload column differs.");
        sql = sql.Replace(payloadColumn, payloadColumn + ",\n    \"ContentRetiredAt\" INTEGER NULL", StringComparison.Ordinal);
        const string copy = "INSERT INTO task_approval_rebuild SELECT * FROM TenantApprovalRequests;";
        if (!sql.Contains(copy, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen Task approval copy differs: " + string.Join(" | ", Regex.Matches(sql, @"(?m)^INSERT INTO[^\n]*").Select(m => m.Value)));
        sql = sql.Replace(copy, "INSERT INTO task_approval_rebuild SELECT " + columns + ",NULL FROM TenantApprovalRequests;", StringComparison.Ordinal);
        sql = Regex.Replace(sql, @"(?m)^(WHEN .*NEW\.OriginalInitiatorPrincipalId IS NULL[^\n]*)$",
            "$1 AND NEW.ContentRetiredAt IS NULL AND json_type(NEW.PayloadJson,'$.Operation.TaskDelegation') IS NOT 'object'");
        sql = Regex.Replace(sql, @"(?m)(^CREATE TRIGGER TR_TenantApprovals_Space_(?:INSERT|Workspace_INSERT)[^\n]*\nWHEN [^\n]*)",
            "$1 AND NOT COALESCE(json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=1,0)");
        return sql + "\n" + Guards;
    }

    private const string Guards = """
        CREATE TRIGGER TR_TaskDelegation_Retirement_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN NEW.ContentRetiredAt IS NOT NULL BEGIN SELECT RAISE(ABORT,'A consent tombstone requires retained original evidence'); END;
        CREATE TRIGGER TR_TaskDelegation_Retirement_Update BEFORE UPDATE ON TenantApprovalRequests
        WHEN NEW.ContentRetiredAt IS NOT OLD.ContentRetiredAt OR OLD.ContentRetiredAt IS NOT NULL
        BEGIN
            SELECT RAISE(ABORT,'Task consent retirement preserves exact immutable metadata') WHERE NOT COALESCE((
                OLD.ContentRetiredAt IS NULL AND NEW.ContentRetiredAt IS NOT NULL AND OLD.Permission='tasks.delegate' AND OLD.Status<>0
                AND json_type(OLD.PayloadJson,'$.Operation.TaskDelegation')='object'
                AND NEW.PayloadJson=json_object('contentRetiredAt',NEW.ContentRetiredAt,
                    'taskId',json_extract(OLD.PayloadJson,'$.Operation.TaskDelegation.Task.TaskId'),
                    'sourceSessionId',json_extract(OLD.PayloadJson,'$.Operation.TaskDelegation.Task.Source.SessionId'),
                    'workerInstanceId',json_extract(OLD.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceId'),
                    'workerPrincipalId',json_extract(OLD.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.PrincipalId'),
                    'actionHash',OLD.Fingerprint)
                AND NEW.RequestId=OLD.RequestId AND NEW.TenantId=OLD.TenantId AND NEW.RunId IS OLD.RunId AND NEW.SessionId IS OLD.SessionId
                AND NEW.AutomationId IS OLD.AutomationId AND NEW.InstanceId=OLD.InstanceId AND NEW.RootActivationId=OLD.RootActivationId
                AND NEW.ExecutorPrincipalId=OLD.ExecutorPrincipalId AND NEW.OriginalInitiatorPrincipalId IS OLD.OriginalInitiatorPrincipalId
                AND NEW.OperationId=OLD.OperationId AND NEW.Fingerprint=OLD.Fingerprint AND NEW.Permission=OLD.Permission
                AND NEW.ResourceType=OLD.ResourceType AND NEW.ResourceId=OLD.ResourceId AND NEW.CredentialId IS OLD.CredentialId
                AND NEW.SandboxProfileId IS OLD.SandboxProfileId AND NEW.SandboxProfileRevision IS OLD.SandboxProfileRevision
                AND NEW.Kind=OLD.Kind AND NEW.Status=OLD.Status AND NEW.Revision=OLD.Revision+1 AND NEW.CreatedAt=OLD.CreatedAt AND NEW.ExpiresAt=OLD.ExpiresAt
                AND EXISTS(SELECT 1 FROM Sessions source WHERE source.SessionId=OLD.ResourceId AND source.DurablyDeletedAtUtc IS NOT NULL)
                AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId AND (r.SessionId=OLD.ResourceId OR r.RootActivationId=OLD.RootActivationId) AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                AND NOT EXISTS(SELECT 1 FROM ApprovalProcessedSignals signal WHERE signal.RequestId=OLD.RequestId AND signal.ProcessedAt IS NULL)
            ),0);
        END;
        CREATE TRIGGER TR_TaskDelegation_Approval_Insert BEFORE INSERT ON TenantApprovalRequests
        WHEN json_type(NEW.PayloadJson,'$.Operation.TaskDelegation')='object'
        BEGIN
            SELECT RAISE(ABORT,'Task consent has exactly one operation payload') WHERE EXISTS (
                SELECT 1 FROM json_each(NEW.PayloadJson,'$.Operation') p WHERE p.key NOT IN
                ('OperationId','InstanceId','ExpectedInstanceRevision','ExpectedPersonaRevision','CanonicalActionJson','ActionHash','SpaceContext','TaskDelegation') AND p.type<>'null');
            SELECT RAISE(ABORT,'Task consent requires its actual source Run and assignment') WHERE json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=0 AND NOT EXISTS (
                SELECT 1 FROM AgentRuns r JOIN TenantSessions s ON s.TenantId=r.TenantId AND s.SessionId=r.SessionId
                JOIN AgentInstances worker ON lower(worker.InstanceId)=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceId')
                JOIN SystemIdentities identity ON identity.TenantId=NEW.TenantId AND identity.AgentInstanceId=worker.InstanceId
                WHERE r.TenantId=NEW.TenantId AND r.AgentRunId=NEW.RunId AND r.SessionId=NEW.SessionId
                    AND r.AgentInstanceId=NEW.InstanceId AND r.ExecutorPrincipalId=NEW.ExecutorPrincipalId AND r.RootActivationId=NEW.RootActivationId
                    AND s.AgentInstanceId=r.AgentInstanceId AND s.ExecutorPrincipalId=r.ExecutorPrincipalId AND r.Status IN (1,2)
                    AND json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActivationId')=r.ActivationId
                    AND json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ParentTaskId') IS json_extract(r.PayloadJson,'$.admission.delegatedTask.taskId')
                    AND identity.Status=0 AND lower(identity.PrincipalId)=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.PrincipalId')
                    AND worker.Revision=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceRevision'));
            SELECT RAISE(ABORT,'Human Task consent requires its actual authenticated command owner')
                WHERE json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=1 AND NOT EXISTS (
                    SELECT 1 FROM TenantSessions s JOIN Sessions saved ON saved.SessionId=s.SessionId
                    JOIN TenantUsers human ON human.TenantId=s.TenantId AND human.PrincipalId=NEW.ExecutorPrincipalId AND human.Status=0
                    JOIN AgentInstances source ON source.InstanceId=s.AgentInstanceId
                    JOIN AgentInstances worker ON lower(worker.InstanceId)=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceId')
                    JOIN SystemIdentities identity ON identity.TenantId=s.TenantId AND identity.AgentInstanceId=worker.InstanceId AND identity.Status=0
                    WHERE s.TenantId=NEW.TenantId AND s.SessionId=NEW.ResourceId AND s.AgentInstanceId=NEW.InstanceId
                        AND saved.DurablyDeletedAtUtc IS NULL AND source.Revision=json_extract(NEW.PayloadJson,'$.Operation.ExpectedInstanceRevision')
                        AND source.PersonaRevision=json_extract(NEW.PayloadJson,'$.Operation.ExpectedPersonaRevision')
                        AND worker.Revision=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.InstanceRevision')
                        AND lower(identity.PrincipalId)=json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment.PrincipalId'));
            SELECT RAISE(ABORT,'Task consent action must bind the frozen delegation') WHERE NOT COALESCE(
                json_extract(json_extract(NEW.PayloadJson,'$.Operation.CanonicalActionJson'),'$.operation')='tasks.delegate'
                AND NOT EXISTS (SELECT fullkey,type,atom FROM json_tree(json_extract(json_extract(NEW.PayloadJson,'$.Operation.CanonicalActionJson'),'$.delegation'))
                    EXCEPT SELECT fullkey,type,atom FROM json_tree(json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation')))
                AND NOT EXISTS (SELECT fullkey,type,atom FROM json_tree(json_extract(NEW.PayloadJson,'$.Operation.TaskDelegation'))
                    EXCEPT SELECT fullkey,type,atom FROM json_tree(json_extract(json_extract(NEW.PayloadJson,'$.Operation.CanonicalActionJson'),'$.delegation'))),0);
        END;
        CREATE TRIGGER TR_TaskDelegation_Approval_Immutable BEFORE UPDATE ON TenantApprovalRequests
        WHEN NEW.ContentRetiredAt IS NULL AND (json_type(NEW.PayloadJson,'$.Operation.TaskDelegation')='object' OR json_type(OLD.PayloadJson,'$.Operation.TaskDelegation')='object')
        BEGIN
            SELECT RAISE(ABORT,'Task consent identity is immutable')
                WHERE json_remove(NEW.PayloadJson,'$.Status','$.Revision') IS NOT json_remove(OLD.PayloadJson,'$.Status','$.Revision');
        END;
        """;
}
