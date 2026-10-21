using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantLearnedMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantLearnedMemories",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemoryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerKey = table.Column<string>(type: "TEXT", nullable: false),
                    UserPrincipalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    RootRunId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceEntryId = table.Column<string>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    ContentHash = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SupersedesMemoryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLearnedMemories", x => new { x.TenantId, x.MemoryId });
                    table.CheckConstraint("CK_TenantLearnedMemory_Owner", "Scope IN (0,1,2) AND ((UserPrincipalId IS NULL AND AccountId IS NULL AND UserId IS NULL AND Scope = 0) OR (UserPrincipalId IS NOT NULL AND AccountId IS NOT NULL AND UserId IS NOT NULL))");
                    table.CheckConstraint("CK_TenantLearnedMemory_Value", "Kind BETWEEN 0 AND 4 AND Status BETWEEN 0 AND 3 AND Revision > 0 AND CreatedAtUtc >= 0 AND UpdatedAtUtc >= CreatedAtUtc AND length(ContentHash) = 64 AND Source IN ('userExplicit','agentInferred') AND length(OwnerKey) BETWEEN 1 AND 160 AND length(Subject) <= 128 AND length(Content) <= 2000 AND (Status = 2 OR (length(Subject) > 0 AND length(Content) > 0))");
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_AgentRuns_RootRunId",
                        column: x => x.RootRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_AgentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_TenantLearnedMemories_TenantId_SupersedesMemoryId",
                        columns: x => new { x.TenantId, x.SupersedesMemoryId },
                        principalTable: "TenantLearnedMemories",
                        principalColumns: new[] { "TenantId", "MemoryId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.SessionId, x.AgentInstanceId, x.ExecutorPrincipalId },
                        principalTable: "TenantSessions",
                        principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_TenantUsers_TenantId_UserPrincipalId_AccountId",
                        columns: x => new { x.TenantId, x.UserPrincipalId, x.AccountId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantLearnedMemories_TenantUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "TenantUsers",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_RootRunId",
                table: "TenantLearnedMemories",
                column: "RootRunId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_RunId",
                table: "TenantLearnedMemories",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_OwnerKey_Kind_SubjectKey",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "OwnerKey", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_OwnerKey_Kind_SubjectKey_Status_UpdatedAtUtc",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "OwnerKey", "Kind", "SubjectKey", "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_SupersedesMemoryId",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "SupersedesMemoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_UserPrincipalId_AccountId",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "UserPrincipalId", "AccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_TenantId_UserPrincipalId_Scope_AgentInstanceId_SessionId_Status",
                table: "TenantLearnedMemories",
                columns: new[] { "TenantId", "UserPrincipalId", "Scope", "AgentInstanceId", "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantLearnedMemories_UserId",
                table: "TenantLearnedMemories",
                column: "UserId");
            migrationBuilder.Sql("""
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
                CREATE TRIGGER TR_TenantLearnedMemory_Delete BEFORE DELETE ON TenantLearnedMemories
                BEGIN SELECT RAISE(ABORT, 'Tombstone learned memory without discarding provenance'); END;
                CREATE TRIGGER TR_TenantUsers_LearnedMemoryOwner_Update BEFORE UPDATE ON TenantUsers
                WHEN (OLD.UserId IS NOT NEW.UserId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId)
                    AND EXISTS (SELECT 1 FROM TenantLearnedMemories m WHERE m.UserId = OLD.UserId)
                BEGIN SELECT RAISE(ABORT, 'Retain the actual learned-memory User owner'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE TenantLearnedMemoryDowngradeGuard (RetainedRows INTEGER CHECK (RetainedRows = 0));
                INSERT INTO TenantLearnedMemoryDowngradeGuard SELECT count(*) FROM TenantLearnedMemories;
                DROP TABLE TenantLearnedMemoryDowngradeGuard;
                DROP TRIGGER TR_TenantUsers_LearnedMemoryOwner_Update;
                """);
            migrationBuilder.DropTable(
                name: "TenantLearnedMemories");
        }
    }
}
