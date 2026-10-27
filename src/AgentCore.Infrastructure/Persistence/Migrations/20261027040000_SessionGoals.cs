using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AgentCore.Infrastructure.Persistence.Migrations;
[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261027040000_SessionGoals")]
public sealed class SessionGoals : Migration
{
    internal static void Build(ModelBuilder model)
    {
        EvaluationSandboxExecutions.Build(model);
        model.Entity("AgentCore.Infrastructure.Persistence.SessionRecord", e => {
            e.Property<string>("FocusedGoalId").HasColumnType("TEXT"); e.Property<long>("GoalFocusRevision").HasColumnType("INTEGER"); e.Property<string>("GoalFocusRequestId").HasColumnType("TEXT"); });
        model.Entity("AgentCore.Infrastructure.Persistence.SessionGoalRow", e => {
            e.Property<Guid>("TenantId").HasColumnType("TEXT"); e.Property<Guid>("GoalId").HasColumnType("TEXT"); e.Property<Guid>("SpaceId").HasColumnType("TEXT");
            e.Property<string>("SessionId").IsRequired().HasColumnType("TEXT"); e.Property<string>("Json").IsRequired().HasColumnType("TEXT"); e.Property<long>("Revision").IsConcurrencyToken().HasColumnType("INTEGER");
            e.HasKey("TenantId", "GoalId"); e.HasAlternateKey("TenantId", "SessionId", "GoalId"); e.HasIndex("TenantId", "SessionId", "GoalId"); e.HasIndex("TenantId", "SpaceId"); e.ToTable("SessionGoals");
            e.HasOne("AgentCore.Infrastructure.Security.TenantSessionRecord", null).WithMany().HasForeignKey("TenantId", "SessionId").HasPrincipalKey("TenantId", "SessionId").OnDelete(DeleteBehavior.Restrict).IsRequired();
            e.HasOne("AgentCore.Infrastructure.Security.SpaceRecord", null).WithMany().HasForeignKey("TenantId", "SpaceId").OnDelete(DeleteBehavior.Restrict).IsRequired(); });
        model.Entity("AgentCore.Infrastructure.Persistence.GoalCheckpointRow", e => {
            foreach (var name in new[] { "TenantId", "GoalId", "CheckpointId" }) e.Property<Guid>(name).HasColumnType("TEXT");
            foreach (var name in new[] { "PlanJson", "StateJson" }) e.Property<string>(name).IsRequired().HasColumnType("TEXT"); e.Property<int>("Ordinal").HasColumnType("INTEGER");
            e.HasKey("TenantId", "GoalId", "CheckpointId"); e.HasIndex("TenantId", "GoalId", "Ordinal").IsUnique(); e.ToTable("GoalCheckpoints");
            e.HasOne("AgentCore.Infrastructure.Persistence.SessionGoalRow", null).WithMany("Checkpoints").HasForeignKey("TenantId", "GoalId").OnDelete(DeleteBehavior.Cascade).IsRequired(); });
        model.Entity("AgentCore.Infrastructure.Persistence.GoalActivityRow", e => {
            foreach (var name in new[] { "TenantId", "GoalId", "ActivityId", "ActorId" }) e.Property<Guid>(name).HasColumnType("TEXT");
            foreach (var name in new[] { "SessionId", "RequestKey", "Fingerprint", "Json", "ReceiptJson" }) e.Property<string>(name).IsRequired().HasColumnType("TEXT");
            e.Property<long>("Revision").HasColumnType("INTEGER"); e.HasKey("TenantId", "ActivityId"); e.HasIndex("TenantId", "SessionId", "ActorId", "RequestKey").IsUnique();
            e.HasIndex("TenantId", "GoalId", "Revision").IsUnique(); e.HasIndex("TenantId", "SessionId", "GoalId"); e.ToTable("GoalActivities");
            e.HasOne("AgentCore.Infrastructure.Persistence.SessionGoalRow", null).WithMany().HasForeignKey("TenantId", "SessionId", "GoalId").HasPrincipalKey("TenantId", "SessionId", "GoalId").OnDelete(DeleteBehavior.Cascade).IsRequired(); });
        model.Entity("AgentCore.Infrastructure.Persistence.SessionGoalRow").Navigation("Checkpoints");
    }
    protected override void BuildTargetModel(ModelBuilder model) => Build(model);
    protected override void Up(MigrationBuilder m)
    {
        m.AddColumn<string>("FocusedGoalId", "Sessions", type: "TEXT", nullable: true);
        m.AddColumn<long>("GoalFocusRevision", "Sessions", type: "INTEGER", nullable: false, defaultValue: 0L);
        m.AddColumn<string>("GoalFocusRequestId", "Sessions", type: "TEXT", nullable: true);
        m.Sql("DROP TRIGGER TR_TenantCore_Fact_Insert;");
        m.Sql(CoreFactGuard(true));
        m.Sql("""
            CREATE TABLE SessionGoals(TenantId TEXT NOT NULL, SessionId TEXT NOT NULL, GoalId TEXT NOT NULL, SpaceId TEXT NOT NULL, Revision INTEGER NOT NULL CHECK(Revision>=1), Json TEXT NOT NULL CHECK(json_valid(Json)),
                PRIMARY KEY(TenantId,GoalId), UNIQUE(TenantId,SessionId,GoalId),
                FOREIGN KEY(TenantId,SessionId) REFERENCES TenantSessions(TenantId,SessionId) ON DELETE RESTRICT,
                FOREIGN KEY(TenantId,SpaceId) REFERENCES Spaces(TenantId,SpaceId) ON DELETE RESTRICT);
            CREATE INDEX IX_SessionGoals_TenantId_SessionId_GoalId ON SessionGoals(TenantId,SessionId,GoalId);
            CREATE INDEX IX_SessionGoals_TenantId_SpaceId ON SessionGoals(TenantId,SpaceId);
            CREATE TABLE GoalCheckpoints(TenantId TEXT NOT NULL, GoalId TEXT NOT NULL, CheckpointId TEXT NOT NULL, Ordinal INTEGER NOT NULL CHECK(Ordinal BETWEEN 0 AND 39), PlanJson TEXT NOT NULL CHECK(json_valid(PlanJson)), StateJson TEXT NOT NULL CHECK(json_valid(StateJson)),
                PRIMARY KEY(TenantId,GoalId,CheckpointId), FOREIGN KEY(TenantId,GoalId) REFERENCES SessionGoals(TenantId,GoalId) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IX_GoalCheckpoints_TenantId_GoalId_Ordinal ON GoalCheckpoints(TenantId,GoalId,Ordinal);
            CREATE TABLE GoalActivities(TenantId TEXT NOT NULL, SessionId TEXT NOT NULL, GoalId TEXT NOT NULL, ActivityId TEXT NOT NULL, ActorId TEXT NOT NULL, RequestKey TEXT NOT NULL, Revision INTEGER NOT NULL, Fingerprint TEXT NOT NULL, Json TEXT NOT NULL CHECK(json_valid(Json)), ReceiptJson TEXT NOT NULL CHECK(json_valid(ReceiptJson)),
                PRIMARY KEY(TenantId,ActivityId), FOREIGN KEY(TenantId,SessionId,GoalId) REFERENCES SessionGoals(TenantId,SessionId,GoalId) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IX_GoalActivities_TenantId_SessionId_ActorId_RequestKey ON GoalActivities(TenantId,SessionId,ActorId,RequestKey);
            CREATE UNIQUE INDEX IX_GoalActivities_TenantId_GoalId_Revision ON GoalActivities(TenantId,GoalId,Revision);
            CREATE INDEX IX_GoalActivities_TenantId_SessionId_GoalId ON GoalActivities(TenantId,SessionId,GoalId);
            CREATE INDEX IX_AgentRuns_GoalWork ON AgentRuns(TenantId,SessionId,json_extract(PayloadJson,'$.admission.goalWork.goalId'),CreatedAtUtc);
            CREATE TEMP TABLE GoalCutoverGuard(Rows INTEGER CONSTRAINT GoalPurposeCannotMap_BackupAndRepairSessionSpaceDescription CHECK(Rows=0));
            INSERT INTO GoalCutoverGuard SELECT count(*) FROM SessionSnapshots p JOIN Sessions s ON s.SessionId=p.SessionId
                WHERE p.PurposeKind='Goal' AND (s.DurablyDeletedAtUtc IS NOT NULL OR p.PurposeDescription IS NULL OR length(trim(p.PurposeDescription))=0
                OR NOT EXISTS(SELECT 1 FROM TenantSessions t JOIN Spaces sp ON sp.TenantId=t.TenantId AND sp.SpaceId=upper(json_extract(s.ContextSelectionJson,'$.spaceId')) WHERE t.SessionId=s.SessionId));
            DROP TABLE GoalCutoverGuard;
            INSERT INTO SessionGoals(TenantId,SessionId,GoalId,SpaceId,Revision,Json)
            SELECT t.TenantId,s.SessionId,upper(s.SessionId),upper(json_extract(s.ContextSelectionJson,'$.spaceId')),1,
                json_object('GoalId',s.SessionId,'TenantId',t.TenantId,'SessionId',s.SessionId,'SpaceId',json_extract(s.ContextSelectionJson,'$.spaceId'),
                    'CreatedByPrincipalId',t.CreatedByPrincipalId,'CreatedAt',strftime('%Y-%m-%dT%H:%M:%fZ',s.CreatedAtUtc/1000.0,'unixepoch'),
                    'UpdatedAt',strftime('%Y-%m-%dT%H:%M:%fZ',s.UpdatedAtUtc/1000.0,'unixepoch'),'Revision',1,'PlanRevision',1,'Status',0,
                    'Plan',json_object('Title',substr(p.PurposeDescription,1,200),'Objective',p.PurposeDescription,'SuccessCriteria',p.PurposeDescription,'Checkpoints',json('[]'),
                        'DeadlineAt',CASE WHEN p.DeadlineAtUtc IS NULL THEN NULL ELSE strftime('%Y-%m-%dT%H:%M:%fZ',p.DeadlineAtUtc/1000.0,'unixepoch') END,
                        'RequireFinalAcceptance',json('true'),'AutoContinuation',json_object('Enabled',json('false'),'MaxRuns',0,'Until',NULL)),
                    'Checkpoints',json('[]'),'AutoRunsUsed',0)
            FROM SessionSnapshots p JOIN Sessions s ON s.SessionId=p.SessionId JOIN TenantSessions t ON t.SessionId=s.SessionId WHERE p.PurposeKind='Goal';
            INSERT INTO GoalCheckpoints(TenantId,GoalId,CheckpointId,Ordinal,PlanJson,StateJson)
            SELECT g.TenantId,g.GoalId,g.GoalId,0,
                json_object('CheckpointId',g.GoalId,'Title','Review imported objective','ExpectedOutcome',p.PurposeDescription,'Required',json('true'),'Dependencies',json('[]'),'Verification',json('[{"Kind":2}]')),
                json_object('CheckpointId',g.GoalId,'Status',0)
            FROM SessionGoals g JOIN SessionSnapshots p ON p.SessionId=g.SessionId WHERE p.PurposeKind='Goal';
            INSERT INTO GoalActivities(TenantId,SessionId,GoalId,ActivityId,ActorId,RequestKey,Revision,Fingerprint,Json,ReceiptJson)
            SELECT g.TenantId,g.SessionId,g.GoalId,g.GoalId,t.CreatedByPrincipalId,'legacy-purpose-cutover',1,lower(hex(zeroblob(32))),
                json_object('ActivityId',g.GoalId,'TenantId',g.TenantId,'SessionId',g.SessionId,'GoalId',g.GoalId,'ActorId',t.CreatedByPrincipalId,
                    'RequestKey','legacy-purpose-cutover','Fingerprint',lower(hex(zeroblob(32))),'Action','ImportedProposed','Revision',1,'PlanRevision',1,'At',json_extract(g.Json,'$.UpdatedAt'),
                    'LegacyMetadata',json(coalesce(p.PurposeMetadataJson,'null'))),
                json_set(g.Json,'$.Plan.Checkpoints',json_array(json(c.PlanJson)),'$.Checkpoints',json_array(json(c.StateJson)))
            FROM SessionGoals g JOIN TenantSessions t ON t.SessionId=g.SessionId JOIN SessionSnapshots p ON p.SessionId=g.SessionId JOIN GoalCheckpoints c ON c.TenantId=g.TenantId AND c.GoalId=g.GoalId;
            UPDATE SessionSnapshots SET PurposeKind='Ongoing',PurposeDescription=NULL,PurposeMetadataJson=NULL WHERE PurposeKind='Goal';
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey) SELECT RoleId,'goals.read' FROM AccessRoles WHERE IsBuiltin=1 AND Key IN ('owner','contributor','reader');
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey) SELECT RoleId,'goals.manage' FROM AccessRoles WHERE IsBuiltin=1 AND Key IN ('owner','contributor');
            INSERT OR IGNORE INTO AccessRolePermissions(RoleId,PermissionKey) SELECT RoleId,'goals.accept' FROM AccessRoles WHERE IsBuiltin=1 AND Key='owner';
            CREATE TRIGGER SessionGoalFocusGuard BEFORE UPDATE OF FocusedGoalId ON Sessions WHEN NEW.FocusedGoalId IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM SessionGoals g JOIN TenantSessions t ON t.TenantId=g.TenantId AND t.SessionId=g.SessionId WHERE g.SessionId=NEW.SessionId AND lower(g.GoalId)=lower(NEW.FocusedGoalId))
                BEGIN SELECT RAISE(ABORT,'Focused Goal must belong to the exact tenant Session'); END;
            CREATE TRIGGER GoalActivityImmutable BEFORE UPDATE ON GoalActivities BEGIN SELECT RAISE(ABORT,'Goal activity is immutable'); END;
            CREATE TRIGGER GoalActivityDeleteGuard BEFORE DELETE ON GoalActivities WHEN NOT EXISTS(SELECT 1 FROM Sessions s WHERE s.SessionId=OLD.SessionId AND s.DurablyDeletedAtUtc IS NOT NULL)
                BEGIN SELECT RAISE(ABORT,'Goal activity may only be retired with its deleted Session'); END;
            CREATE TRIGGER SessionGoalOwnerImmutable BEFORE UPDATE ON SessionGoals WHEN NEW.TenantId<>OLD.TenantId OR NEW.SessionId<>OLD.SessionId OR NEW.GoalId<>OLD.GoalId OR NEW.SpaceId<>OLD.SpaceId OR NEW.Revision<>OLD.Revision+1
                BEGIN SELECT RAISE(ABORT,'Goal owner is immutable and writes require CAS'); END;
            """);
    }
    private static string CoreFactGuard(bool goals)
    {
        using var stream = typeof(SessionGoals).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025060000_ContextSuccessor.Previous.sql")
            ?? throw new InvalidOperationException("Frozen Core fact predecessor is missing.");
        using var reader = new StreamReader(stream); var previous = reader.ReadToEnd();
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(previous))) != "71de1999d9653efba4e075af65c5ebd94cc9890852e1a5fa1ceebe24195d339c")
            throw new InvalidOperationException("Frozen Core fact predecessor changed.");
        return previous.Replace("WHEN 9 THEN 'ApprovalContinuation' END", "WHEN 9 THEN 'ApprovalContinuation' WHEN 10 THEN 'ContextContinuation'" + (goals ? " WHEN 11 THEN 'GoalContinuation'" : "") + " END", StringComparison.Ordinal);
    }
    protected override void Down(MigrationBuilder m)
    {
        m.Sql("""
            CREATE TEMP TABLE GoalRollbackGuard(Rows INTEGER CONSTRAINT RestoreCoordinatedBackup_GoalWritesCannotRollback CHECK(Rows=0));
            INSERT INTO GoalRollbackGuard SELECT count(*) FROM SessionGoals;
            DROP TABLE GoalRollbackGuard;
            DROP TRIGGER SessionGoalFocusGuard; DROP TRIGGER GoalActivityImmutable; DROP TRIGGER GoalActivityDeleteGuard; DROP TRIGGER SessionGoalOwnerImmutable;
            DROP TABLE GoalActivities; DROP TABLE GoalCheckpoints; DROP TABLE SessionGoals; DROP INDEX IX_AgentRuns_GoalWork;
            DELETE FROM AccessRolePermissions WHERE PermissionKey IN ('goals.read','goals.manage','goals.accept');
            """);
        m.Sql("DROP TRIGGER TR_TenantCore_Fact_Insert;");
        m.Sql(CoreFactGuard(false));
        m.Sql("ALTER TABLE Sessions DROP COLUMN FocusedGoalId; ALTER TABLE Sessions DROP COLUMN GoalFocusRevision; ALTER TABLE Sessions DROP COLUMN GoalFocusRequestId;");
    }
}
