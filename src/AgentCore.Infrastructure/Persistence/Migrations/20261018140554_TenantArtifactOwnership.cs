using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantArtifactOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Keep trigger creation after the SQLite rebuild. EF's deferred constraint
            // rebuild would otherwise drop triggers emitted earlier in this migration.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Artifacts" (
                    "ArtifactId" TEXT NOT NULL CONSTRAINT "PK_Artifacts" PRIMARY KEY,
                    "SessionId" TEXT NOT NULL,
                    "BlobKey" TEXT NOT NULL,
                    "DisplayName" TEXT NOT NULL,
                    "ContentType" TEXT NOT NULL,
                    "ByteSize" INTEGER NOT NULL,
                    "Sha256Hex" TEXT NOT NULL,
                    "SourceAttachmentId" TEXT NULL,
                    "AgentRunId" TEXT NULL,
                    "WorkspaceLogicalPath" TEXT NULL,
                    "CreatedAtUtc" INTEGER NOT NULL,
                    "TenantId" TEXT NULL,
                    CONSTRAINT "FK_Artifacts_TenantSessions_TenantId_SessionId" FOREIGN KEY ("TenantId", "SessionId")
                        REFERENCES "TenantSessions" ("TenantId", "SessionId") ON DELETE RESTRICT,
                    CONSTRAINT "CK_Artifacts_TenantOwner" CHECK (TenantId IS NULL OR
                        (AgentRunId IS NOT NULL AND SourceAttachmentId IS NULL AND WorkspaceLogicalPath IS NULL
                        AND ByteSize >= 0 AND length(Sha256Hex) = 64 AND BlobKey = 'tenants/'
                        || lower(replace(TenantId, '-', '')) || '/' || lower(replace(SessionId, '-', ''))
                        || '/' || lower(replace(ArtifactId, '-', ''))))
                );
                INSERT INTO "ef_temp_Artifacts" (ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                    SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc)
                SELECT ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                    SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc FROM Artifacts;
                DROP TABLE "Artifacts";
                ALTER TABLE "ef_temp_Artifacts" RENAME TO "Artifacts";
                CREATE INDEX "IX_Artifacts_SessionId" ON "Artifacts" ("SessionId");
                CREATE INDEX "IX_Artifacts_SessionId_AgentRunId" ON "Artifacts" ("SessionId", "AgentRunId");
                CREATE INDEX "IX_Artifacts_TenantId_SessionId_CreatedAtUtc_ArtifactId"
                    ON "Artifacts" ("TenantId", "SessionId", "CreatedAtUtc", "ArtifactId");
                """);

            // A nullable legacy provenance ID cannot express this conditional composite FK.
            // Owned publications must reference the actual tenant Run and remain immutable.
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Artifacts_TenantRun_Insert BEFORE INSERT ON Artifacts
                WHEN NEW.TenantId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'owned Artifact requires its actual tenant Run')
                    WHERE NOT EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId
                        AND r.AgentRunId = NEW.AgentRunId AND r.SessionId = NEW.SessionId);
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Artifacts_TenantRun_Update BEFORE UPDATE ON Artifacts
                WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'owned Artifact is immutable') WHERE OLD.TenantId IS NOT NULL;
                    SELECT RAISE(ABORT, 'owned Artifact requires its actual tenant Run')
                    WHERE NOT EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId
                        AND r.AgentRunId = NEW.AgentRunId AND r.SessionId = NEW.SessionId);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never downgrade away durable ownership after owned publication.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ArtifactOwnershipRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows = 0));
                INSERT INTO ArtifactOwnershipRollbackGuard SELECT COUNT(*) FROM Artifacts WHERE TenantId IS NOT NULL;
                DROP TABLE ArtifactOwnershipRollbackGuard;
                """);
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Artifacts_TenantRun_Insert;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS TR_Artifacts_TenantRun_Update;");
            migrationBuilder.DropForeignKey(
                name: "FK_Artifacts_TenantSessions_TenantId_SessionId",
                table: "Artifacts");

            migrationBuilder.DropIndex(
                name: "IX_Artifacts_TenantId_SessionId_CreatedAtUtc_ArtifactId",
                table: "Artifacts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Artifacts_TenantOwner",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Artifacts");
        }
    }
}
