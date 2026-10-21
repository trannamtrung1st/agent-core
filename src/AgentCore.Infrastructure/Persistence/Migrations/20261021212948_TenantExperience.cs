using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantExperiences",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExperienceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", nullable: false),
                    UserPrincipalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    RootRunId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ThroughCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceWitnessJson = table.Column<string>(type: "TEXT", nullable: false),
                    ContentJson = table.Column<string>(type: "TEXT", nullable: true),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Visibility = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceExperienceIdsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantExperiences", x => new { x.TenantId, x.ExperienceId });
                    table.CheckConstraint("CK_TenantExperience_Owner", "((UserPrincipalId IS NULL AND AccountId IS NULL AND UserId IS NULL AND OwnerKey='instance') OR (UserPrincipalId IS NOT NULL AND AccountId IS NOT NULL AND UserId IS NOT NULL AND OwnerKey='user:' || lower(UserPrincipalId)))");
                    table.CheckConstraint("CK_TenantExperience_Source", "((SourceKind IN (0,1) AND ThroughCursor>0 AND json_type(SourceWitnessJson)='object' AND json_extract(SourceWitnessJson,'$.Kind')=SourceKind AND json_extract(SourceWitnessJson,'$.SourceId')=lower(SourceId) AND json_extract(SourceWitnessJson,'$.ThroughCursor')=ThroughCursor AND SourceExperienceIdsJson IS NULL) OR (SourceKind=2 AND ThroughCursor=0 AND SourceId=ExperienceId AND SourceWitnessJson='[]' AND SourceExperienceIdsJson IS NOT NULL AND length(SourceExperienceIdsJson) BETWEEN 2 AND 320 AND json_valid(SourceExperienceIdsJson) AND json_type(SourceExperienceIdsJson)='array' AND json_array_length(SourceExperienceIdsJson) BETWEEN 2 AND 8))");
                    table.CheckConstraint("CK_TenantExperience_Value", "SourceKind IN (0,1,2) AND Visibility IN (0,1,2,3) AND Revision > 0 AND CreatedAtUtc >= 0 AND UpdatedAtUtc >= CreatedAtUtc AND length(ContentHash)=64 AND ContentHash NOT GLOB '*[^0-9a-f]*' AND length(SourceWitnessJson) BETWEEN 2 AND 8192 AND json_valid(SourceWitnessJson) AND ((Visibility=2 AND ContentJson IS NULL) OR (Visibility<>2 AND ContentJson IS NOT NULL AND length(CAST(ContentJson AS BLOB)) BETWEEN 2 AND 36000 AND json_valid(ContentJson) AND json_type(ContentJson)='object'))");
                    table.ForeignKey(
                        name: "FK_TenantExperiences_AgentRuns_RootRunId",
                        column: x => x.RootRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExperiences_AgentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExperiences_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId },
                        principalTable: "TenantSessions",
                        principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExperiences_TenantUsers_TenantId_UserPrincipalId_AccountId",
                        columns: x => new { x.TenantId, x.UserPrincipalId, x.AccountId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantExperiences_TenantUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "TenantUsers",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_RootRunId",
                table: "TenantExperiences",
                column: "RootRunId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_RunId",
                table: "TenantExperiences",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_TenantId_AgentInstanceId_OwnerKey_SourceKind_SourceId_ThroughCursor",
                table: "TenantExperiences",
                columns: new[] { "TenantId", "AgentInstanceId", "OwnerKey", "SourceKind", "SourceId", "ThroughCursor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_TenantId_AgentInstanceId_UserPrincipalId_Visibility_UpdatedAtUtc",
                table: "TenantExperiences",
                columns: new[] { "TenantId", "AgentInstanceId", "UserPrincipalId", "Visibility", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "TenantExperiences",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_TenantId_UserPrincipalId_AccountId",
                table: "TenantExperiences",
                columns: new[] { "TenantId", "UserPrincipalId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantExperiences_UserId",
                table: "TenantExperiences",
                column: "UserId");
                    migrationBuilder.Sql(Guards);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE TenantExperienceDowngradeGuard (Rows INTEGER CHECK(Rows=0));
                INSERT INTO TenantExperienceDowngradeGuard SELECT count(*) FROM TenantExperiences;
                DROP TABLE TenantExperienceDowngradeGuard;
                """);
            migrationBuilder.DropTable(
                name: "TenantExperiences");
        }
        private const string Guards = """
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
            CREATE TRIGGER TR_TenantExperience_Delete BEFORE DELETE ON TenantExperiences
            BEGIN SELECT RAISE(ABORT,'Retain Experience provenance through a content-free tombstone'); END;
            """;
    }
}
