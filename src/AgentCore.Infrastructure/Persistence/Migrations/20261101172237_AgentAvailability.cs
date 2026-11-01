using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class AgentAvailability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- Native DROP COLUMN preserves cross-table ownership triggers. Stop all writers first.
        DROP TRIGGER TR_AgentDecommission_Instance;
        UPDATE AgentInstances SET Lifecycle='Disabled', Revision=Revision+1
            WHERE Lifecycle='Archived' OR ShowInChat=0;
        UPDATE Principals SET Status=1 WHERE Status<>2 AND PrincipalId IN
            (SELECT PrincipalId FROM SystemIdentities WHERE AgentInstanceId IN
                (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled') AND Status<>2);
        UPDATE SystemIdentities SET Status=1 WHERE Status<>2 AND AgentInstanceId IN
            (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        UPDATE Tenants SET Revision=Revision+1 WHERE TenantId IN
            (SELECT TenantId FROM SystemIdentities WHERE AgentInstanceId IN
                (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        -- Preserve exact effect/checkpoint evidence. Re-enabling never replays old work.
        UPDATE AgentRuns SET Status=6, Revision=Revision+1, NextRetryAtUtc=NULL, LeaseExpiresAtUtc=NULL,
            UpdatedAtUtc=max(UpdatedAtUtc,CAST(unixepoch('subsec')*1000 AS INTEGER)),
            PayloadJson=json_set(PayloadJson,'$.claim',NULL,'$.wait',NULL,'$.cancellationRequested',json('true'),
                '$.cancellationRequestedAtUtc',coalesce(json_extract(PayloadJson,'$.cancellationRequestedAtUtc'),
                    strftime('%Y-%m-%dT%H:%M:%f+00:00','now')))
            WHERE Status NOT IN (4,5,6) AND AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        UPDATE Sessions SET Revision=Revision+1,UpdatedAtUtc=max(UpdatedAtUtc,CAST(unixepoch('subsec')*1000 AS INTEGER))
            WHERE EXISTS(SELECT 1 FROM ConversationEntries e JOIN AgentRuns r ON r.SessionId=e.SessionId
                AND json_extract(r.PayloadJson,'$.admission.responseId')=e.ResponseId
                WHERE e.SessionId=Sessions.SessionId AND e.Role='Assistant' AND e.Status='Streaming' AND r.Status=6
                    AND r.AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        UPDATE ConversationEntries SET Status='Interrupted',InterruptReason='agentDisabled',
            CompletedAtUtc=coalesce(CompletedAtUtc,CAST(unixepoch('subsec')*1000 AS INTEGER))
            WHERE Role='Assistant' AND Status='Streaming' AND EXISTS(SELECT 1 FROM AgentRuns r
                WHERE r.SessionId=ConversationEntries.SessionId AND json_extract(r.PayloadJson,'$.admission.responseId')=ConversationEntries.ResponseId
                    AND r.Status=6 AND r.AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        UPDATE TenantOperationGrants SET Status=3, Revision=Revision+1 WHERE Status=0 AND RequestId IN
            (SELECT RequestId FROM TenantApprovalRequests WHERE InstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        UPDATE TenantApprovalRequests SET Status=CASE WHEN ExpiresAt<=CAST(unixepoch('subsec')*1000 AS INTEGER) THEN 3 ELSE 4 END,
            Revision=Revision+1, PayloadJson=json_set(PayloadJson,'$.Status',CASE WHEN ExpiresAt<=CAST(unixepoch('subsec')*1000 AS INTEGER) THEN 3 ELSE 4 END,'$.Revision',Revision+1)
            WHERE Status=0 AND InstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        CREATE TEMP TABLE DisabledPendingTasks AS
            SELECT TenantId,TaskId,SourceSessionId,
                lower(hex(randomblob(4))||'-'||hex(randomblob(2))||'-4'||substr(hex(randomblob(2)),2)||'-8'||substr(hex(randomblob(2)),2)||'-'||hex(randomblob(6))) AS NoticeId
            FROM TenantDelegatedTasks WHERE State=0 AND (WorkerInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled')
                OR json_extract(Json,'$.Source.InstanceId') IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        UPDATE TenantDelegatedTasks SET State=4,Revision=Revision+1,UpdatedAt=max(UpdatedAt,CAST(unixepoch('subsec')*1000 AS INTEGER)),
            Json=json_set(Json,'$.State',4,'$.Revision',Revision+1,'$.Reason','agent_disabled','$.CancellationRequested',json('true'),
                '$.UpdatedAt',strftime('%Y-%m-%dT%H:%M:%f+00:00',max(UpdatedAt,CAST(unixepoch('subsec')*1000 AS INTEGER))/1000.0,'unixepoch'))
            WHERE TaskId IN (SELECT TaskId FROM DisabledPendingTasks);
        INSERT INTO TenantTaskNotifications(TenantId,TaskId,NotificationId,TerminalRunId,RecipientSessionId,Revision,Json)
            SELECT TenantId,TaskId,NoticeId,NULL,SourceSessionId,1,json_object('TenantId',TenantId,'TaskId',TaskId,'NotificationId',NoticeId,
                'TerminalRunId',NULL,'RecipientSessionId',SourceSessionId,'Revision',1,'CreatedAt',strftime('%Y-%m-%dT%H:%M:%f+00:00','now'))
            FROM DisabledPendingTasks WHERE SourceSessionId IS NOT NULL;
        DROP TABLE DisabledPendingTasks;
        UPDATE TenantAgentMessages SET ReceiptJson=json_set(ReceiptJson,'$.State',3,'$.Reason','agent_disabled',
            '$.Revision',json_extract(ReceiptJson,'$.Revision')+1,'$.UpdatedAt',strftime('%Y-%m-%dT%H:%M:%f+00:00','now'))
            WHERE json_extract(ReceiptJson,'$.State')=0 AND (lower(CAST(RecipientInstanceId AS TEXT)) IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled')
                OR json_extract(Json,'$.SenderInstanceId') IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled'));
        UPDATE CoreEventDeliveries SET Status=4,Code='agent_disabled' WHERE (Status IN (0,1,6,9) OR Status=3 AND json_valid(DecisionJson) AND coalesce(json_extract(DecisionJson,'$.Retryable'),json_extract(DecisionJson,'$.retryable'))=1)
            AND AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        UPDATE ExternalEventDeliveries SET Status=2,Code='agent_disabled' WHERE (Status IN (0,5,6) OR Status=4 AND json_valid(DecisionJson) AND coalesce(json_extract(DecisionJson,'$.Retryable'),json_extract(DecisionJson,'$.retryable'))=1)
            AND AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        UPDATE CoreEventBuckets SET Flushed=1,PayloadJson=json_set(PayloadJson,'$.Flushed',json('true'),'$.CompletionCode','agent_disabled')
            WHERE Flushed=0 AND AgentInstanceId IN (SELECT InstanceId FROM AgentInstances WHERE Lifecycle='Disabled');
        ALTER TABLE AgentInstances DROP COLUMN ShowInChat;
        CREATE TRIGGER TR_AgentDecommission_Instance BEFORE UPDATE ON AgentInstances
        WHEN NEW.Lifecycle<>'Disabled' AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.AgentInstanceId=OLD.InstanceId AND i.Kind=0 AND i.Status=2)
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TEMP TABLE AgentAvailabilityRollbackGuard (OwnedRows INTEGER CHECK(OwnedRows=0));
        INSERT INTO AgentAvailabilityRollbackGuard SELECT count(*) FROM SystemIdentities WHERE Kind=0 AND TenantId IS NOT NULL AND Status=2;
        DROP TABLE AgentAvailabilityRollbackGuard;
        DROP TRIGGER TR_AgentDecommission_Instance;
        ALTER TABLE AgentInstances ADD COLUMN ShowInChat INTEGER NOT NULL DEFAULT 1;
        UPDATE AgentInstances SET Lifecycle='Archived',ShowInChat=0 WHERE Lifecycle='Disabled';
        CREATE TRIGGER TR_AgentDecommission_Instance BEFORE UPDATE ON AgentInstances
        WHEN NEW.Lifecycle<>'Archived' AND EXISTS(SELECT 1 FROM SystemIdentities i WHERE i.AgentInstanceId=OLD.InstanceId AND i.Kind=0 AND i.Status=2)
        BEGIN SELECT RAISE(ABORT, 'Decommissioned Agent cannot be restored'); END;
        """);
}
