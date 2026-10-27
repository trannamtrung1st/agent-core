using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261027060000_TaskDelegation")]
public sealed class TaskDelegation : Migration
{
    internal static void Build(ModelBuilder model)
    {
        SessionGoals.Build(model);
        model.Entity("AgentCore.Infrastructure.Security.TenantApprovalRecord").ToTable("TenantApprovalRequests", table => table.HasCheckConstraint("CK_TenantApprovalRequest_Values", TaskApprovalSchema.Values));
        model.Entity("AgentCore.Infrastructure.Persistence.AgentInstanceRecord").Property<string>("CooperationJson").HasColumnType("TEXT");
        model.Entity("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("TaskId").HasColumnType("TEXT");
            e.Property<Guid>("RootActivationId").HasColumnType("TEXT");
            e.Property<Guid?>("ParentTaskId").HasColumnType("TEXT");
            e.Property<string>("SourceSessionId").IsRequired().HasColumnType("TEXT");
            e.Property<string>("OperationScope").IsRequired().HasColumnType("TEXT");
            e.Property<string>("RequestKey").IsRequired().HasColumnType("TEXT");
            e.Property<string>("RequestHash").IsRequired().HasColumnType("TEXT");
            e.Property<string>("WorkerInstanceId").IsRequired().HasColumnType("TEXT");
            e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.Property<string>("WorkerSessionId").HasColumnType("TEXT");
            e.Property<int>("State").HasColumnType("INTEGER");
            e.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
            e.Property<long>("UpdatedAt").HasColumnType("INTEGER");
            e.HasKey("TenantId", "TaskId");
            e.HasIndex("TenantId", "SourceSessionId", "OperationScope", "RequestKey").IsUnique();
            e.HasIndex("TenantId", "WorkerInstanceId", "State", "TaskId"); e.HasIndex("State", "UpdatedAt", "TaskId");
            e.HasIndex("TenantId", "RootActivationId", "TaskId"); e.HasIndex("TenantId", "ParentTaskId"); e.HasIndex("TenantId", "WorkerSessionId");
            e.HasOne("AgentCore.Infrastructure.Security.TenantSessionRecord", null).WithMany().HasForeignKey("TenantId", "SourceSessionId").HasPrincipalKey("TenantId", "SessionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne("AgentCore.Infrastructure.Security.TenantSessionRecord", null).WithMany().HasForeignKey("TenantId", "WorkerSessionId").HasPrincipalKey("TenantId", "SessionId").OnDelete(DeleteBehavior.Restrict);
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "ParentTaskId").OnDelete(DeleteBehavior.Restrict);
            e.ToTable("TenantDelegatedTasks", t => t.HasCheckConstraint("CK_Task_State", "State BETWEEN 0 AND 8 AND Revision >= 1"));
        });
        model.Entity("AgentCore.Infrastructure.Persistence.TaskDependencyRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("TaskId").HasColumnType("TEXT");
            e.Property<Guid>("PrerequisiteId").HasColumnType("TEXT");
            e.HasKey("TenantId", "TaskId", "PrerequisiteId");
            e.HasIndex("TenantId", "PrerequisiteId");
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "TaskId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "PrerequisiteId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.ToTable("TenantTaskDependencies");
        });
        model.Entity("AgentCore.Infrastructure.Persistence.AgentThreadRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("ThreadId").HasColumnType("TEXT");
            e.Property<Guid>("FirstInstanceId").HasColumnType("TEXT");
            e.Property<Guid>("SecondInstanceId").HasColumnType("TEXT");
            e.Property<Guid?>("TaskId").HasColumnType("TEXT");
            e.Property<long>("Sequence").IsConcurrencyToken().HasColumnType("INTEGER");
            e.Property<long?>("RetiredAt").HasColumnType("INTEGER");
            e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.HasKey("TenantId", "ThreadId");
            e.HasIndex("TenantId", "FirstInstanceId", "SecondInstanceId").IsUnique().HasFilter("TaskId IS NULL AND RetiredAt IS NULL");
            e.HasIndex("TenantId", "TaskId").IsUnique().HasFilter("TaskId IS NOT NULL");
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "TaskId").OnDelete(DeleteBehavior.Cascade);
            e.ToTable("TenantAgentThreads", t => t.HasCheckConstraint("CK_Thread_Parties", "TaskId IS NOT NULL OR FirstInstanceId < SecondInstanceId"));
        });
        model.Entity("AgentCore.Infrastructure.Persistence.AgentMessageRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("ThreadId").HasColumnType("TEXT");
            e.Property<Guid>("MessageId").HasColumnType("TEXT");
            e.Property<Guid>("SenderPrincipalId").HasColumnType("TEXT");
            e.Property<Guid>("RecipientInstanceId").HasColumnType("TEXT");
            e.Property<long>("Sequence").HasColumnType("INTEGER");
            e.Property<string>("SourceScope").IsRequired().HasColumnType("TEXT");
            e.Property<string>("RequestKey").IsRequired().HasColumnType("TEXT");
            e.Property<string>("RequestHash").IsRequired().HasColumnType("TEXT");
            e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.Property<string>("ReceiptJson").IsRequired().HasColumnType("TEXT");
            e.HasKey("TenantId", "MessageId");
            e.HasIndex("TenantId", "ThreadId", "Sequence").IsUnique(); e.HasIndex("TenantId", "SenderPrincipalId", "SourceScope", "RequestKey").IsUnique();
            e.HasIndex("TenantId", "RecipientInstanceId", "MessageId");
            e.HasOne("AgentCore.Infrastructure.Persistence.AgentThreadRow", null).WithMany().HasForeignKey("TenantId", "ThreadId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            e.ToTable("TenantAgentMessages");
        });
        model.Entity("AgentCore.Infrastructure.Security.TenantApprovalRecord", e => e.Property<long?>("ContentRetiredAt").HasColumnType("INTEGER"));
        model.Entity("AgentCore.Infrastructure.Security.TenantOperationReceiptRecord", e => e.Property<string>("TaskAdmissionJson").HasColumnType("TEXT"));
        model.Entity("AgentCore.Infrastructure.Persistence.TaskNotificationRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("TaskId").HasColumnType("TEXT");
            e.Property<Guid>("NotificationId").HasColumnType("TEXT");
            e.Property<Guid?>("TerminalRunId").HasColumnType("TEXT");
            e.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
            e.Property<string>("RecipientSessionId").IsRequired().HasColumnType("TEXT");
            e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.HasKey("TenantId", "NotificationId");
            e.HasIndex("TenantId", "TaskId").IsUnique();
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "TaskId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            e.ToTable("TenantTaskNotifications");
        });
        model.Entity("AgentCore.Infrastructure.Persistence.HumanTaskApprovalOutcomeRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT"); e.Property<Guid>("OperationId").HasColumnType("TEXT");
            e.Property<Guid>("RequestId").HasColumnType("TEXT"); e.Property<Guid?>("TaskId").HasColumnType("TEXT");
            e.Property<long>("RecordedAt").HasColumnType("INTEGER"); e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.HasKey("TenantId", "OperationId"); e.HasIndex("TenantId", "RequestId").IsUnique();
            e.HasIndex("TenantId", "TaskId");
            e.HasOne("AgentCore.Infrastructure.Security.TenantApprovalRecord", null).WithMany().HasForeignKey("TenantId", "RequestId").HasPrincipalKey("TenantId", "RequestId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "TaskId").OnDelete(DeleteBehavior.Restrict);
            e.ToTable("TenantTaskApprovalOutcomes");
        });
        model.Entity("AgentCore.Infrastructure.Persistence.TaskCommandRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT");
            e.Property<Guid>("TaskId").HasColumnType("TEXT");
            e.Property<Guid>("ActorId").HasColumnType("TEXT");
            e.Property<string>("RequestKey").IsRequired().HasColumnType("TEXT");
            e.Property<string>("Hash").IsRequired().HasColumnType("TEXT");
            e.Property<string>("Json").IsRequired().HasColumnType("TEXT");
            e.HasKey("TenantId", "TaskId", "ActorId", "RequestKey");
            e.HasOne("AgentCore.Infrastructure.Persistence.DelegatedTaskRow", null).WithMany().HasForeignKey("TenantId", "TaskId").OnDelete(DeleteBehavior.Cascade).IsRequired();
            e.ToTable("TenantTaskCommands");
        });
    }
    protected override void BuildTargetModel(ModelBuilder model) => Build(model);
    protected override void Up(MigrationBuilder m)
    {
        // No convert-on-read, runtime alias or silent retirement of installed child state.
        m.Sql("""
            CREATE TEMP TABLE TaskCutoverGuard(Rows INTEGER CONSTRAINT task_cutover_reset_required CHECK(Rows=0));
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentInstances WHERE DefinitionId='general-assistant' AND ActiveVersion BETWEEN 21 AND 34;
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM Sessions WHERE json_extract(OriginJson,'$.kind')=1 OR json_extract(OriginJson,'$.Kind')=1;
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM TenantApprovalRequests WHERE
                json_extract(PayloadJson,'$.Operation.BackgroundStart') IS NOT NULL OR json_extract(PayloadJson,'$.Operation.CompletionRead') IS NOT NULL;
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentRuns WHERE json_extract(PayloadJson,'$.wait.mode')=1;
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM BackgroundCompletionReceipts WHERE Status IN (1,2)
                OR ClaimRunId IS NOT NULL OR ClaimExpiresAtUtc IS NOT NULL OR json_extract(InboxJson,'$.status') IN (1,2)
                OR json_extract(InboxJson,'$.claimRunId') IS NOT NULL OR json_extract(InboxJson,'$.claimGeneration') IS NOT NULL
                OR json_extract(InboxJson,'$.claimToolCallId') IS NOT NULL OR json_extract(InboxJson,'$.claimToken') IS NOT NULL
                OR json_extract(InboxJson,'$.claimExpiresAtUtc') IS NOT NULL
                OR json_extract(InboxJson,'$.acknowledgment') IS NOT NULL OR json_extract(InboxJson,'$.handledByRunId') IS NOT NULL;
            UPDATE BackgroundCompletionReceipts SET InboxJson=json_remove(InboxJson,'$.claimRunId','$.claimGeneration',
                '$.claimToolCallId','$.claimToken','$.claimExpiresAtUtc','$.acknowledgment','$.handledByRunId') WHERE InboxJson IS NOT NULL;
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentDefinitionDrafts d, json_tree(d.CandidateJson) j
                WHERE j.value IN ('background.start','background.list','background.inspect','background.take','background.acknowledge')
                AND (lower(j.fullkey) LIKE '%.executionpolicy.capabilities.resolvedcapabilities[%'
                    OR lower(j.fullkey) LIKE '%.executionpolicy.toolallowlist[%' OR lower(j.fullkey) LIKE '%.executionpolicy.projection.alwayscapabilities[%');
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentDefinitionPublications d, json_tree(d.PayloadJson) j
                WHERE j.value IN ('background.start','background.list','background.inspect','background.take','background.acknowledge')
                AND (lower(j.fullkey) LIKE '%.executionpolicy.capabilities.resolvedcapabilities[%'
                    OR lower(j.fullkey) LIKE '%.executionpolicy.toolallowlist[%' OR lower(j.fullkey) LIKE '%.executionpolicy.projection.alwayscapabilities[%');
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentInstances a, json_tree(COALESCE(a.SettingsOverridesJson,'{}')) j
                WHERE j.value IN ('background.start','background.list','background.inspect','background.take','background.acknowledge')
                AND (lower(j.fullkey) LIKE '$.selectedcapabilities.value[%' OR lower(j.fullkey) LIKE '$.alwayscapabilities.value[%');
            INSERT INTO TaskCutoverGuard SELECT count(*) FROM AgentRuns WHERE json_array_length(json_extract(PayloadJson,'$.wait.backgroundSessionIds')) > 0;
            UPDATE AgentRuns SET PayloadJson=json_remove(PayloadJson,'$.wait.backgroundSessionIds') WHERE json_extract(PayloadJson,'$.wait.mode')=0;
            DROP TABLE TaskCutoverGuard;
            """);
        m.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        m.Sql(TaskApprovalSchema.Rebuild());
        m.AddColumn<string>("CooperationJson", "AgentInstances", type: "TEXT", nullable: true);
        m.AddColumn<string>(name: "TaskAdmissionJson", table: "TenantOperationReceipts", type: "TEXT", nullable: true);
        m.Sql("DROP TRIGGER TR_TenantCore_Fact_Insert;");
        m.Sql(CoreFactGuard(true));
        m.Sql("""
            CREATE TABLE TenantDelegatedTasks(TenantId TEXT NOT NULL, TaskId TEXT NOT NULL, SourceSessionId TEXT NOT NULL,
                OperationScope TEXT NOT NULL, RequestKey TEXT NOT NULL, RequestHash TEXT NOT NULL CHECK(length(RequestHash)=64),
                WorkerInstanceId TEXT NOT NULL, WorkerSessionId TEXT NULL, RootActivationId TEXT NOT NULL, ParentTaskId TEXT NULL,
                State INTEGER NOT NULL, Revision INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, Json TEXT NOT NULL CHECK(json_valid(Json)),
                CONSTRAINT CK_Task_State CHECK(State BETWEEN 0 AND 8 AND Revision>=1), PRIMARY KEY(TenantId,TaskId),
                FOREIGN KEY(TenantId,SourceSessionId) REFERENCES TenantSessions(TenantId,SessionId) ON DELETE RESTRICT,
                FOREIGN KEY(TenantId,WorkerSessionId) REFERENCES TenantSessions(TenantId,SessionId) ON DELETE RESTRICT,
                FOREIGN KEY(TenantId,ParentTaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IX_TenantDelegatedTasks_TenantId_SourceSessionId_OperationScope_RequestKey ON TenantDelegatedTasks(TenantId,SourceSessionId,OperationScope,RequestKey);
            CREATE INDEX IX_TenantDelegatedTasks_TenantId_WorkerInstanceId_State_TaskId ON TenantDelegatedTasks(TenantId,WorkerInstanceId,State,TaskId);
            CREATE INDEX IX_TenantDelegatedTasks_State_UpdatedAt_TaskId ON TenantDelegatedTasks(State,UpdatedAt,TaskId);
            CREATE INDEX IX_TenantDelegatedTasks_TenantId_RootActivationId_TaskId ON TenantDelegatedTasks(TenantId,RootActivationId,TaskId);
            CREATE INDEX IX_TenantDelegatedTasks_TenantId_ParentTaskId ON TenantDelegatedTasks(TenantId,ParentTaskId);
            CREATE INDEX IX_TenantDelegatedTasks_TenantId_WorkerSessionId ON TenantDelegatedTasks(TenantId,WorkerSessionId);
            CREATE TABLE TenantTaskDependencies(TenantId TEXT NOT NULL, TaskId TEXT NOT NULL, PrerequisiteId TEXT NOT NULL,
                PRIMARY KEY(TenantId,TaskId,PrerequisiteId), CHECK(TaskId<>PrerequisiteId),
                FOREIGN KEY(TenantId,TaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE CASCADE,
                FOREIGN KEY(TenantId,PrerequisiteId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE RESTRICT);
            CREATE INDEX IX_TenantTaskDependencies_TenantId_PrerequisiteId ON TenantTaskDependencies(TenantId,PrerequisiteId);
            CREATE TABLE TenantAgentThreads(TenantId TEXT NOT NULL, ThreadId TEXT NOT NULL, FirstInstanceId TEXT NOT NULL,
                SecondInstanceId TEXT NOT NULL, TaskId TEXT NULL, Sequence INTEGER NOT NULL CHECK(Sequence>=0), RetiredAt INTEGER NULL, Json TEXT NOT NULL CHECK(json_valid(Json)),
                PRIMARY KEY(TenantId,ThreadId), CONSTRAINT CK_Thread_Parties CHECK(TaskId IS NOT NULL OR FirstInstanceId<SecondInstanceId),
                FOREIGN KEY(TenantId,TaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IX_TenantAgentThreads_TenantId_FirstInstanceId_SecondInstanceId ON TenantAgentThreads(TenantId,FirstInstanceId,SecondInstanceId) WHERE TaskId IS NULL AND RetiredAt IS NULL;
            CREATE UNIQUE INDEX IX_TenantAgentThreads_TenantId_TaskId ON TenantAgentThreads(TenantId,TaskId) WHERE TaskId IS NOT NULL;
            CREATE TABLE TenantAgentMessages(TenantId TEXT NOT NULL, ThreadId TEXT NOT NULL, MessageId TEXT NOT NULL,
                SenderPrincipalId TEXT NOT NULL, SourceScope TEXT NOT NULL, RecipientInstanceId TEXT NOT NULL,
                RequestKey TEXT NOT NULL, RequestHash TEXT NOT NULL CHECK(length(RequestHash)=64), Sequence INTEGER NOT NULL CHECK(Sequence>=1),
                Json TEXT NOT NULL CHECK(json_valid(Json)), ReceiptJson TEXT NOT NULL CHECK(json_valid(ReceiptJson)), PRIMARY KEY(TenantId,MessageId),
                FOREIGN KEY(TenantId,ThreadId) REFERENCES TenantAgentThreads(TenantId,ThreadId) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IX_TenantAgentMessages_TenantId_ThreadId_Sequence ON TenantAgentMessages(TenantId,ThreadId,Sequence);
            CREATE UNIQUE INDEX IX_TenantAgentMessages_TenantId_SenderPrincipalId_SourceScope_RequestKey ON TenantAgentMessages(TenantId,SenderPrincipalId,SourceScope,RequestKey);
            CREATE INDEX IX_TenantAgentMessages_TenantId_RecipientInstanceId_MessageId ON TenantAgentMessages(TenantId,RecipientInstanceId,MessageId);
            CREATE TABLE TenantTaskNotifications(TenantId TEXT NOT NULL, TaskId TEXT NOT NULL, NotificationId TEXT NOT NULL, TerminalRunId TEXT NULL,
                RecipientSessionId TEXT NOT NULL, Revision INTEGER NOT NULL CHECK(Revision>=1), Json TEXT NOT NULL CHECK(json_valid(Json)),
                PRIMARY KEY(TenantId,NotificationId), FOREIGN KEY(TenantId,TaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IX_TenantTaskNotifications_TenantId_TaskId ON TenantTaskNotifications(TenantId,TaskId);
            CREATE UNIQUE INDEX IX_TenantApprovalRequests_TaskDelegationRequest ON TenantApprovalRequests(TenantId,
                json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Source.OperationScope'),
                json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.Request.RequestKey'))
                WHERE json_type(PayloadJson,'$.Operation.TaskDelegation')='object';
            CREATE UNIQUE INDEX IX_TenantApprovalRequests_TaskDelegationId ON TenantApprovalRequests(TenantId,
                json_extract(PayloadJson,'$.Operation.TaskDelegation.Task.TaskId'))
                WHERE json_type(PayloadJson,'$.Operation.TaskDelegation')='object';
            CREATE TABLE TenantTaskApprovalOutcomes(TenantId TEXT NOT NULL, OperationId TEXT NOT NULL, RequestId TEXT NOT NULL,
                TaskId TEXT NULL, RecordedAt INTEGER NOT NULL, Json TEXT NOT NULL CHECK(json_valid(Json)), PRIMARY KEY(TenantId,OperationId),
                FOREIGN KEY(TenantId,RequestId) REFERENCES TenantApprovalRequests(TenantId,RequestId) ON DELETE RESTRICT,
                FOREIGN KEY(TenantId,TaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IX_TenantTaskApprovalOutcomes_TenantId_RequestId ON TenantTaskApprovalOutcomes(TenantId,RequestId);
            CREATE INDEX IX_TenantTaskApprovalOutcomes_TenantId_TaskId ON TenantTaskApprovalOutcomes(TenantId,TaskId);
            CREATE TRIGGER HumanTaskApprovalOutcomeGuard BEFORE INSERT ON TenantTaskApprovalOutcomes BEGIN
                SELECT RAISE(ABORT,'Human Task outcome requires its exact decided command') WHERE NOT COALESCE((
                    lower(NEW.TenantId)=json_extract(NEW.Json,'$.TenantId') AND lower(NEW.OperationId)=json_extract(NEW.Json,'$.OperationId')
                    AND lower(NEW.RequestId)=json_extract(NEW.Json,'$.RequestId')
                    AND json_extract(NEW.Json,'$.Admitted')=(NEW.TaskId IS NOT NULL)
                    AND EXISTS (SELECT 1 FROM TenantApprovalRequests a WHERE a.TenantId=NEW.TenantId AND a.RequestId=NEW.RequestId
                        AND a.OperationId=NEW.OperationId AND a.Status IN (1,2,3,4)
                        AND json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Source.ActorKind')=1
                        AND (NEW.TaskId IS NULL AND json_extract(NEW.Json,'$.TaskAdmission') IS NULL
                            OR (a.Status=1 AND NEW.RecordedAt<a.ExpiresAt AND json_extract(NEW.Json,'$.ReasonCode')='task_created'
                                AND lower(NEW.TaskId)=json_extract(NEW.Json,'$.TaskAdmission.TaskId') AND json_extract(NEW.Json,'$.TaskAdmission.Revision')=1
                                AND EXISTS (SELECT 1 FROM TenantDelegatedTasks t WHERE t.TenantId=NEW.TenantId AND t.TaskId=NEW.TaskId
                                    AND t.Revision=1 AND t.State IN (0,1)
                                    AND lower(t.TaskId)=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.TaskId')
                                    AND json_extract(t.Json,'$.Source')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Source')
                                    AND json_extract(t.Json,'$.Request')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Request')
                                    AND json_extract(t.Json,'$.Assignment')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment')
                                    AND t.WorkerSessionId IS json_extract(NEW.Json,'$.TaskAdmission.WorkerSessionId')
                                    AND json_extract(t.Json,'$.InitialWorkerRunId') IS json_extract(NEW.Json,'$.TaskAdmission.InitialWorkerRunId')))))),0);
            END;
            CREATE TRIGGER HumanTaskApprovalOutcomeImmutable BEFORE UPDATE ON TenantTaskApprovalOutcomes
                BEGIN SELECT RAISE(ABORT,'Human Task admission evidence is immutable'); END;
            CREATE TABLE TenantTaskCommands(TenantId TEXT NOT NULL, TaskId TEXT NOT NULL, ActorId TEXT NOT NULL, RequestKey TEXT NOT NULL,
                Hash TEXT NOT NULL CHECK(length(Hash)=64), Json TEXT NOT NULL CHECK(json_valid(Json)), PRIMARY KEY(TenantId,TaskId,ActorId,RequestKey),
                FOREIGN KEY(TenantId,TaskId) REFERENCES TenantDelegatedTasks(TenantId,TaskId) ON DELETE CASCADE);
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey)
                SELECT r.RoleId,p.Permission FROM AccessRoles r CROSS JOIN
                (SELECT 'tasks.read' Permission UNION ALL SELECT 'tasks.delegate' UNION ALL SELECT 'tasks.cancel' UNION ALL SELECT 'agent-messages.read' UNION ALL SELECT 'agent-messages.send') p
                WHERE r.IsBuiltin=1 AND r.Key='owner' AND r.HumanOnly=1;
            CREATE TRIGGER TaskAssignmentImmutable BEFORE UPDATE ON TenantDelegatedTasks WHEN (
                NEW.TenantId<>OLD.TenantId OR NEW.TaskId<>OLD.TaskId OR NEW.SourceSessionId<>OLD.SourceSessionId OR NEW.OperationScope<>OLD.OperationScope
                OR NEW.RequestKey<>OLD.RequestKey OR NEW.RequestHash<>OLD.RequestHash OR NEW.WorkerInstanceId<>OLD.WorkerInstanceId
                OR NEW.RootActivationId<>OLD.RootActivationId OR NEW.ParentTaskId IS NOT OLD.ParentTaskId OR NEW.Revision<>OLD.Revision+1
                OR OLD.State>=4 OR (OLD.WorkerSessionId IS NOT NULL AND NEW.WorkerSessionId IS NOT OLD.WorkerSessionId)
                OR json_extract(NEW.Json,'$.Assignment')<>json_extract(OLD.Json,'$.Assignment')
                OR json_extract(NEW.Json,'$.Source')<>json_extract(OLD.Json,'$.Source') OR json_extract(NEW.Json,'$.Request')<>json_extract(OLD.Json,'$.Request')
                ) AND NOT COALESCE((
                    OLD.State>=4 AND NEW.State=OLD.State AND NEW.TenantId=OLD.TenantId AND NEW.TaskId=OLD.TaskId
                    AND NEW.SourceSessionId=OLD.SourceSessionId AND NEW.OperationScope=OLD.OperationScope AND NEW.RequestKey=OLD.RequestKey
                    AND NEW.RequestHash=OLD.RequestHash AND NEW.WorkerInstanceId=OLD.WorkerInstanceId AND NEW.RootActivationId=OLD.RootActivationId
                    AND NEW.ParentTaskId IS OLD.ParentTaskId AND NEW.WorkerSessionId IS OLD.WorkerSessionId AND NEW.UpdatedAt=OLD.UpdatedAt AND NEW.Revision=OLD.Revision+1
                    AND json_extract(OLD.Json,'$.ContentRetiredAt') IS NULL AND json_type(NEW.Json,'$.ContentRetiredAt')='text'
                    AND json_extract(NEW.Json,'$.Request.Objective')='[retired]' AND json_extract(NEW.Json,'$.Request.Title')='[retired]'
                    AND json_extract(NEW.Json,'$.Request.RetiredFingerprint')=OLD.RequestHash
                    AND json_extract(NEW.Json,'$.Result') IS CASE WHEN json_extract(OLD.Json,'$.Result') IS NULL THEN NULL ELSE '[retired]' END
                    AND json_remove(NEW.Json,'$.Revision','$.Request','$.Result','$.ContentRetiredAt')=json_remove(OLD.Json,'$.Revision','$.Request','$.Result','$.ContentRetiredAt')
                    AND json_remove(json_extract(NEW.Json,'$.Request'),'$.Objective','$.Title','$.RetiredFingerprint')=json_remove(json_extract(OLD.Json,'$.Request'),'$.Objective','$.Title','$.RetiredFingerprint')
                    AND EXISTS(SELECT 1 FROM Sessions s WHERE s.SessionId=OLD.SourceSessionId AND s.DurablyDeletedAtUtc IS NOT NULL)
                    AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId AND (r.SessionId=OLD.SourceSessionId OR lower(r.RootActivationId)=lower(OLD.RootActivationId)) AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                ),0) BEGIN SELECT RAISE(ABORT,'Task assignment and terminal facts are immutable'); END;
            CREATE TRIGGER AgentMessageImmutable BEFORE UPDATE ON TenantAgentMessages WHEN (NEW.Json<>OLD.Json OR NEW.Sequence<>OLD.Sequence
                OR NEW.ThreadId<>OLD.ThreadId OR NEW.SenderPrincipalId<>OLD.SenderPrincipalId OR NEW.RecipientInstanceId<>OLD.RecipientInstanceId
                OR NEW.SourceScope<>OLD.SourceScope OR NEW.RequestKey<>OLD.RequestKey OR NEW.RequestHash<>OLD.RequestHash
                ) AND NOT COALESCE((NEW.TenantId=OLD.TenantId AND NEW.MessageId=OLD.MessageId AND NEW.Sequence=OLD.Sequence
                    AND NEW.ThreadId=OLD.ThreadId AND NEW.SenderPrincipalId=OLD.SenderPrincipalId AND NEW.RecipientInstanceId=OLD.RecipientInstanceId
                    AND NEW.SourceScope=OLD.SourceScope AND NEW.RequestKey=OLD.RequestKey AND NEW.RequestHash=OLD.RequestHash
                    AND json_extract(OLD.Json,'$.ContentRetiredAt') IS NULL AND json_type(NEW.Json,'$.ContentRetiredAt')='text'
                    AND json_extract(NEW.Json,'$.Text')='[retired]'
                    AND json_remove(NEW.Json,'$.Text','$.ContentRetiredAt')=json_remove(OLD.Json,'$.Text','$.ContentRetiredAt')
                    AND EXISTS(SELECT 1 FROM Sessions s WHERE s.SessionId=json_extract(OLD.Json,'$.SourceSessionId') AND s.DurablyDeletedAtUtc IS NOT NULL)
                    AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId AND r.SessionId=json_extract(OLD.Json,'$.SourceSessionId') AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                    AND NOT EXISTS(SELECT 1 FROM AgentRuns r WHERE r.TenantId=OLD.TenantId AND r.AgentRunId=json_extract(OLD.ReceiptJson,'$.WakeRunId') AND (r.Status NOT IN (4,5,6) OR json_extract(r.PayloadJson,'$.sideEffect.disposition') IN (1,2,5)))
                ),0) BEGIN SELECT RAISE(ABORT,'Agent message content and provenance are immutable'); END;
            CREATE TRIGGER TaskAdmissionReceiptImmutable BEFORE UPDATE ON TenantOperationReceipts
                WHEN NEW.TaskAdmissionJson IS NOT OLD.TaskAdmissionJson
                BEGIN SELECT RAISE(ABORT,'Task admission receipt is immutable'); END;
            CREATE TRIGGER TaskAdmissionReceiptRequired BEFORE INSERT ON TenantOperationReceipts
                WHEN NEW.Outcome=0 AND NEW.TaskAdmissionJson IS NULL AND EXISTS (SELECT 1 FROM TenantApprovalRequests a
                    WHERE a.TenantId=NEW.TenantId AND a.RequestId=NEW.RequestId AND json_type(a.PayloadJson,'$.Operation.TaskDelegation')='object')
                BEGIN SELECT RAISE(ABORT,'A completed Task admission requires its exact receipt'); END;
            CREATE TRIGGER TaskAdmissionReceiptGuard BEFORE INSERT ON TenantOperationReceipts WHEN NEW.TaskAdmissionJson IS NOT NULL BEGIN
                SELECT RAISE(ABORT,'Task receipt requires its exact admitted assignment') WHERE NOT COALESCE((
                    NEW.RequestId IS NOT NULL AND NEW.Outcome=0 AND NEW.ReasonCode='task_created'
                    AND length(CAST(NEW.TaskAdmissionJson AS BLOB)) BETWEEN 2 AND 1024 AND json_valid(NEW.TaskAdmissionJson)
                    AND json_extract(NEW.TaskAdmissionJson,'$.Revision')=1
                    AND EXISTS (SELECT 1 FROM TenantDelegatedTasks t JOIN TenantApprovalRequests a ON a.TenantId=t.TenantId
                        WHERE a.RequestId=NEW.RequestId AND t.TenantId=NEW.TenantId AND lower(t.TaskId)=lower(json_extract(NEW.TaskAdmissionJson,'$.TaskId'))
                            AND lower(t.TaskId)=lower(json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.TaskId'))
                            AND t.Revision=1 AND t.State IN (0,1)
                            AND t.WorkerSessionId IS json_extract(NEW.TaskAdmissionJson,'$.WorkerSessionId')
                            AND json_extract(t.Json,'$.InitialWorkerRunId') IS json_extract(NEW.TaskAdmissionJson,'$.InitialWorkerRunId')
                            AND json_extract(t.Json,'$.Source')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Source')
                            AND json_extract(t.Json,'$.Request')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Request')
                            AND json_extract(t.Json,'$.Assignment')=json_extract(a.PayloadJson,'$.Operation.TaskDelegation.Task.Assignment'))),0);
            END;
            """);
        m.Sql("DROP TRIGGER TR_TenantCore_RunCausation_Insert;");
        m.Sql(RunCausationGuard(true));
        m.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TEMP TABLE TaskRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_task_backup_required CHECK(Rows=0));
            INSERT INTO TaskRollbackGuard SELECT count(*) FROM TenantDelegatedTasks;
            INSERT INTO TaskRollbackGuard SELECT count(*) FROM TenantAgentMessages;
            INSERT INTO TaskRollbackGuard SELECT count(*) FROM TenantTaskApprovalOutcomes;
            INSERT INTO TaskRollbackGuard SELECT count(*) FROM TenantApprovalRequests WHERE Permission='tasks.delegate';
            INSERT INTO TaskRollbackGuard SELECT count(*) FROM TenantOperationReceipts WHERE TaskAdmissionJson IS NOT NULL;
            DROP TABLE TaskRollbackGuard;
            DROP INDEX IX_TenantApprovalRequests_TaskDelegationRequest;
            DROP INDEX IX_TenantApprovalRequests_TaskDelegationId;
            DROP TRIGGER TaskAdmissionReceiptImmutable;
            DROP TRIGGER TaskAdmissionReceiptRequired;
            DROP TRIGGER TaskAdmissionReceiptGuard;
            DROP TRIGGER TR_TenantCore_Fact_Insert;
            DROP TRIGGER TR_TenantCore_RunCausation_Insert;
            DROP TABLE TenantTaskApprovalOutcomes;
            DROP TABLE TenantTaskCommands; DROP TABLE TenantTaskNotifications; DROP TABLE TenantAgentMessages;
            DROP TABLE TenantAgentThreads; DROP TABLE TenantTaskDependencies; DROP TABLE TenantDelegatedTasks;
            DELETE FROM AccessRolePermissions WHERE PermissionKey IN ('tasks.read','tasks.delegate','tasks.cancel','agent-messages.read','agent-messages.send');
            """);
        // Native SQLite DROP COLUMN preserves incumbent cross-table triggers.
        // EF's table rebuild temporarily removes AgentInstances and invalidates them.
        m.Sql("ALTER TABLE TenantOperationReceipts DROP COLUMN TaskAdmissionJson;");
        m.Sql("ALTER TABLE AgentInstances DROP COLUMN CooperationJson;");
        m.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
        m.Sql(TaskApprovalSchema.Rebuild(tasks: false));
        m.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        m.Sql(CoreFactGuard(false));
        m.Sql(RunCausationGuard(false));
    }
    private static string CoreFactGuard(bool tasks)
    {
        using var stream = typeof(TaskDelegation).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025060000_ContextSuccessor.Previous.sql")
            ?? throw new InvalidOperationException("Frozen Core fact predecessor is missing.");
        using var reader = new StreamReader(stream); var previous = reader.ReadToEnd();
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(previous))) != "71de1999d9653efba4e075af65c5ebd94cc9890852e1a5fa1ceebe24195d339c")
            throw new InvalidOperationException("Frozen Core fact predecessor changed.");
        if (tasks)
        {
            const string ownedRoot = "r.TenantId = NEW.TenantId AND r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId AND r.AgentRunId = json_extract(NEW.PayloadJson, '$.RootAgentRunId')";
            const string taskRoot = """
                r.TenantId = NEW.TenantId AND r.AgentRunId = json_extract(NEW.PayloadJson, '$.RootAgentRunId')
                AND ((r.AgentInstanceId = NEW.AgentInstanceId AND r.ExecutorPrincipalId = NEW.ExecutorPrincipalId)
                    OR (json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') AND EXISTS(
                        SELECT 1 FROM AgentRuns producer JOIN TenantDelegatedTasks task ON task.TenantId=producer.TenantId
                            AND lower(task.TaskId)=lower(coalesce(json_extract(producer.PayloadJson,'$.admission.delegatedTask.taskId'),json_extract(producer.PayloadJson,'$.admission.taskReport.taskId')))
                            AND lower(task.RootActivationId)=lower(r.ActivationId)
                        WHERE producer.TenantId=NEW.TenantId AND producer.AgentInstanceId=NEW.AgentInstanceId AND producer.ExecutorPrincipalId=NEW.ExecutorPrincipalId
                            AND producer.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
                            AND lower(producer.RootActivationId)=lower(r.ActivationId))))
                """;
            if (!previous.Contains(ownedRoot, StringComparison.Ordinal)) throw new InvalidOperationException("Frozen Core root guard is missing.");
            previous = previous.Replace(ownedRoot, taskRoot, StringComparison.Ordinal);
        }
        return previous.Replace("WHEN 9 THEN 'ApprovalContinuation' END", "WHEN 9 THEN 'ApprovalContinuation' WHEN 10 THEN 'ContextContinuation' WHEN 11 THEN 'GoalContinuation'"
            + (tasks ? " WHEN 12 THEN 'DelegatedTask' WHEN 13 THEN 'TaskCompletion' WHEN 14 THEN 'AgentMessage'" : "") + " END", StringComparison.Ordinal);
    }
    private static string RunCausationGuard(bool tasks)
    {
        const string ordinary = """
            EXISTS (SELECT 1 FROM AgentRuns source JOIN AgentRuns root ON root.ActivationId=source.RootActivationId
                AND root.TenantId=source.TenantId AND root.AgentInstanceId=source.AgentInstanceId AND root.ExecutorPrincipalId=source.ExecutorPrincipalId
                LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.ExecutionSessionId=root.SessionId AND o.TenantId=root.TenantId
                    AND o.AgentInstanceId=root.AgentInstanceId AND o.ExecutorPrincipalId=root.ExecutorPrincipalId AND o.Disposition=6
                WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
                    AND root.RootActivationId=root.ActivationId
                    AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=0)
                        OR (o.SourceKind IN (0,1,2,3) AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=CASE o.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END
                            AND json_extract(root.PayloadJson,'$.admission.activation.triggerOccurrenceId') IS o.OccurrenceId))
                    AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
                    AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
                    AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]'))
            """;
        const string delegated = """
            EXISTS (SELECT 1 FROM AgentRuns source
                JOIN TenantDelegatedTasks task ON task.TenantId=source.TenantId AND lower(task.TaskId)=lower(json_extract(source.PayloadJson,'$.admission.delegatedTask.taskId'))
                    AND task.WorkerSessionId=source.SessionId AND task.WorkerInstanceId=source.AgentInstanceId
                    AND lower(json_extract(task.Json,'$.Assignment.PrincipalId'))=lower(source.ExecutorPrincipalId)
                    AND lower(task.RootActivationId)=lower(source.RootActivationId)
                JOIN AgentRuns initial ON initial.AgentRunId=json_extract(task.Json,'$.InitialWorkerRunId') AND initial.TenantId=task.TenantId
                    AND initial.SessionId=source.SessionId AND initial.AgentInstanceId=source.AgentInstanceId AND initial.ExecutorPrincipalId=source.ExecutorPrincipalId
                    AND json_extract(initial.PayloadJson,'$.admission.activation.kind')=12 AND json_extract(initial.PayloadJson,'$.admission.executionContext.origin')=12
                    AND json_extract(initial.PayloadJson,'$.admission.delegatedTask.assignmentHash')=json_extract(source.PayloadJson,'$.admission.delegatedTask.assignmentHash')
                JOIN AgentRuns root ON root.TenantId=task.TenantId AND lower(root.ActivationId)=lower(task.RootActivationId) AND root.RootActivationId=root.ActivationId
                LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.ExecutionSessionId=root.SessionId AND o.TenantId=root.TenantId
                    AND o.AgentInstanceId=root.AgentInstanceId AND o.ExecutorPrincipalId=root.ExecutorPrincipalId AND o.Disposition=6
                WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
                    AND ((json_extract(root.PayloadJson,'$.admission.executionContext.origin')=0 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=0)
                        OR (o.SourceKind IN (0,1,2,3) AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=CASE o.SourceKind WHEN 0 THEN 4 WHEN 1 THEN 5 WHEN 2 THEN 7 WHEN 3 THEN 6 END)
                        OR (json_extract(root.PayloadJson,'$.admission.executionContext.origin')=12 AND json_extract(root.PayloadJson,'$.admission.activation.kind')=12
                            AND EXISTS(SELECT 1 FROM TenantDelegatedTasks humanTask WHERE humanTask.TenantId=root.TenantId
                                AND json_extract(humanTask.Json,'$.InitialWorkerRunId')=root.AgentRunId AND json_extract(humanTask.Json,'$.Source.ActorKind')=1
                                AND json_type(humanTask.Json,'$.Source.RunId')='null' AND json_type(humanTask.Json,'$.Source.HumanCommandId')='text')))
                    AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
                    AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
                    AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]'))
            """;
        const string taskReport = """
            EXISTS (SELECT 1 FROM AgentRuns source
                JOIN TenantTaskNotifications notice ON notice.TenantId=source.TenantId
                    AND lower(notice.NotificationId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.notificationId'))
                JOIN TenantDelegatedTasks task ON task.TenantId=notice.TenantId AND task.TaskId=notice.TaskId
                    AND lower(task.TaskId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.taskId'))
                    AND task.Revision=json_extract(source.PayloadJson,'$.admission.taskReport.taskRevision')
                    AND lower(task.RootActivationId)=lower(source.RootActivationId)
                    AND task.SourceSessionId=source.SessionId
                    AND lower(json_extract(task.Json,'$.Source.InstanceId'))=lower(source.AgentInstanceId)
                    AND json_extract(task.Json,'$.Request.ReportOnCompletion')=1
                    AND lower(notice.TerminalRunId)=lower(json_extract(source.PayloadJson,'$.admission.taskReport.terminalRunId'))
                JOIN TenantSessions parentOwner ON parentOwner.TenantId=source.TenantId
                    AND parentOwner.SessionId=source.SessionId AND parentOwner.AgentInstanceId=source.AgentInstanceId AND parentOwner.ExecutorPrincipalId=source.ExecutorPrincipalId
                JOIN AgentRuns report ON report.AgentRunId=json_extract(notice.Json,'$.HandlingRunId')
                    AND report.TenantId=source.TenantId AND report.SessionId=source.SessionId AND report.AgentInstanceId=source.AgentInstanceId
                    AND report.ExecutorPrincipalId=source.ExecutorPrincipalId AND json_extract(report.PayloadJson,'$.admission.activation.kind')=13
                    AND json_extract(report.PayloadJson,'$.admission.taskReport')=json_extract(source.PayloadJson,'$.admission.taskReport')
                JOIN AgentRuns root ON root.TenantId=task.TenantId AND lower(root.ActivationId)=lower(task.RootActivationId) AND root.RootActivationId=root.ActivationId
                LEFT JOIN TriggerOccurrences o ON o.AcceptedAgentRunId=root.AgentRunId AND o.TenantId=root.TenantId AND o.Disposition=6
                WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
                    AND (json_extract(root.PayloadJson,'$.admission.activation.kind')=0
                        OR o.SourceKind IN (0,1,2,3)
                        OR (json_extract(root.PayloadJson,'$.admission.activation.kind')=12
                            AND EXISTS(SELECT 1 FROM TenantDelegatedTasks humanTask WHERE humanTask.TenantId=root.TenantId
                                AND json_extract(humanTask.Json,'$.InitialWorkerRunId')=root.AgentRunId AND json_extract(humanTask.Json,'$.Source.ActorKind')=1
                                AND json_type(humanTask.Json,'$.Source.RunId')='null' AND json_type(humanTask.Json,'$.Source.HumanCommandId')='text')))
                    AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.rootAgentRunId'),root.AgentRunId)
                    AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.triggerDepth'),0)
                    AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds') IS coalesce(json_extract(o.EvidenceJson,'$.triggerContext.causation.visitedAutomationIds'),'[]'))
            """;
        const string messageWake = """
            EXISTS(SELECT 1 FROM AgentRuns source
                JOIN AgentRuns root ON root.TenantId=source.TenantId AND root.ActivationId=source.RootActivationId
                    AND root.AgentInstanceId=source.AgentInstanceId AND root.ExecutorPrincipalId=source.ExecutorPrincipalId AND root.SessionId=source.SessionId
                    AND root.RootActivationId=root.ActivationId AND json_extract(root.PayloadJson,'$.admission.activation.kind')=14
                    AND json_extract(root.PayloadJson,'$.admission.executionContext.origin')=14
                JOIN TenantAgentMessages message ON message.TenantId=root.TenantId
                    AND lower(message.MessageId)=lower(json_extract(root.PayloadJson,'$.admission.agentMessage.messageId'))
                    AND lower(message.RecipientInstanceId)=lower(root.AgentInstanceId)
                    AND json_extract(message.ReceiptJson,'$.WakeRunId')=root.AgentRunId AND json_extract(message.ReceiptJson,'$.WakeSessionId')=root.SessionId
                    AND json_extract(message.ReceiptJson,'$.WakeHops') BETWEEN 1 AND 2
                    AND json_extract(root.PayloadJson,'$.admission.agentMessage')=json_extract(source.PayloadJson,'$.admission.agentMessage')
                JOIN TenantAgentThreads thread ON thread.TenantId=message.TenantId AND thread.ThreadId=message.ThreadId AND thread.TaskId IS NULL
                WHERE source.TenantId=NEW.TenantId AND source.AgentRunId=json_extract(json_extract(NEW.PayloadJson,'$.DataJson'),'$.agentRunId')
                    AND json_extract(NEW.PayloadJson,'$.RootAgentRunId') IS root.AgentRunId
                    AND json_extract(NEW.PayloadJson,'$.TriggerDepth')=0 AND json_extract(NEW.PayloadJson,'$.VisitedAutomationIds')='[]')
            """;
        return "CREATE TRIGGER TR_TenantCore_RunCausation_Insert BEFORE INSERT ON CoreEvents WHEN NEW.TenantId IS NOT NULL AND json_extract(NEW.PayloadJson,'$.Key') IN ('run.completed','run.failed') BEGIN SELECT RAISE(ABORT,'Core Run causation requires its actual accepted root') WHERE NOT COALESCE(("
            + ordinary + (tasks ? " OR " + delegated + " OR " + taskReport + " OR " + messageWake : "") + "),0); END;";
    }
}
