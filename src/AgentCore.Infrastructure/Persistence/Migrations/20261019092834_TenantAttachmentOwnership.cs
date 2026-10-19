using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantAttachmentOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Rebuild explicitly before creating triggers; EF deferred rebuilds drop earlier triggers.
            migrationBuilder.Sql("""
                CREATE TABLE ef_temp_Attachments (
                    AttachmentId TEXT NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
                    SessionId TEXT NOT NULL,
                    BlobKey TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    ContentType TEXT NOT NULL,
                    ByteSize INTEGER NOT NULL,
                    Sha256Hex TEXT NOT NULL,
                    State TEXT NOT NULL,
                    Readable INTEGER NOT NULL,
                    EntryId TEXT NULL,
                    StageForNextTurn INTEGER NOT NULL,
                    CreatedAtUtc INTEGER NOT NULL,
                    ExpiresAtUtc INTEGER NULL,
                    BoundAtUtc INTEGER NULL,
                    TenantId TEXT NULL,
                    UploadRequestId TEXT NULL,
                    UploadedByPrincipalId TEXT NULL,
                    ContentDeletedAtUtc INTEGER NULL,
                    CONSTRAINT CK_Attachments_TenantOwner CHECK (TenantId IS NULL OR (UploadRequestId IS NOT NULL AND UploadedByPrincipalId IS NOT NULL AND ByteSize BETWEEN 0 AND 26214400 AND length(Sha256Hex) = 64 AND Sha256Hex NOT GLOB '*[^0-9a-f]*' AND BlobKey = 'tenants/' || lower(replace(TenantId, '-', '')) || '/' || lower(replace(SessionId, '-', '')) || '/' || lower(replace(AttachmentId, '-', '')) AND ((State = 'Pending' AND EntryId IS NULL AND ExpiresAtUtc IS NOT NULL AND BoundAtUtc IS NULL AND StageForNextTurn = 0) OR (State = 'Bound' AND EntryId IS NOT NULL AND ExpiresAtUtc IS NULL AND BoundAtUtc IS NOT NULL)))),
                    CONSTRAINT FK_Attachments_Principals_UploadedByPrincipalId FOREIGN KEY (UploadedByPrincipalId) REFERENCES Principals (PrincipalId) ON DELETE RESTRICT,
                    CONSTRAINT FK_Attachments_TenantSessions_TenantId_SessionId FOREIGN KEY (TenantId,SessionId) REFERENCES TenantSessions (TenantId,SessionId) ON DELETE RESTRICT
                );
                INSERT INTO ef_temp_Attachments (AttachmentId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,State,Readable,EntryId,StageForNextTurn,CreatedAtUtc,ExpiresAtUtc,BoundAtUtc) SELECT AttachmentId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,State,Readable,EntryId,StageForNextTurn,CreatedAtUtc,ExpiresAtUtc,BoundAtUtc FROM Attachments;
                DROP TABLE Attachments;
                ALTER TABLE ef_temp_Attachments RENAME TO Attachments;
                CREATE INDEX IX_Attachments_SessionId ON Attachments (SessionId);
                CREATE INDEX IX_Attachments_State_ExpiresAtUtc ON Attachments (State,ExpiresAtUtc);
                CREATE INDEX IX_Attachments_UploadedByPrincipalId ON Attachments (UploadedByPrincipalId);
                CREATE INDEX IX_Attachments_TenantId_SessionId_CreatedAtUtc_AttachmentId ON Attachments (TenantId,SessionId,CreatedAtUtc,AttachmentId);
                CREATE UNIQUE INDEX IX_Attachments_TenantId_SessionId_UploadRequestId ON Attachments (TenantId,SessionId,UploadRequestId);
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Attachments_Tenant_Insert BEFORE INSERT ON Attachments
                WHEN (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
                 OR (NEW.TenantId IS NOT NULL AND (NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
                 OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
                 OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
                BEGIN SELECT RAISE(ABORT, 'Attachment ownership mismatch'); END;
                CREATE TRIGGER TR_Attachments_Tenant_Update BEFORE UPDATE ON Attachments
                WHEN OLD.TenantId IS NOT NEW.TenantId
                 OR (NEW.TenantId IS NULL AND EXISTS (SELECT 1 FROM TenantSessions WHERE SessionId = NEW.SessionId))
                 OR (OLD.TenantId IS NOT NULL AND (OLD.SessionId IS NOT NEW.SessionId OR OLD.AttachmentId IS NOT NEW.AttachmentId
                 OR OLD.UploadRequestId IS NOT NEW.UploadRequestId OR OLD.UploadedByPrincipalId IS NOT NEW.UploadedByPrincipalId
                 OR OLD.BlobKey IS NOT NEW.BlobKey OR OLD.DisplayName IS NOT NEW.DisplayName OR OLD.ContentType IS NOT NEW.ContentType
                 OR OLD.ByteSize IS NOT NEW.ByteSize OR OLD.Sha256Hex IS NOT NEW.Sha256Hex OR OLD.Readable IS NOT NEW.Readable OR OLD.CreatedAtUtc IS NOT NEW.CreatedAtUtc
                 OR (OLD.State = 'Bound' AND (NEW.State IS NOT 'Bound' OR OLD.EntryId IS NOT NEW.EntryId OR OLD.BoundAtUtc IS NOT NEW.BoundAtUtc))
                 OR (OLD.State = 'Pending' AND NEW.State = 'Pending' AND NEW.ExpiresAtUtc > OLD.ExpiresAtUtc)
                 OR NOT EXISTS (SELECT 1 FROM TenantSessions WHERE TenantId = NEW.TenantId AND SessionId = NEW.SessionId)
                 OR NOT EXISTS (SELECT 1 FROM Principals WHERE PrincipalId = NEW.UploadedByPrincipalId AND TenantId = NEW.TenantId AND Kind = 0)
                 OR (NEW.State = 'Bound' AND NOT EXISTS (SELECT 1 FROM ConversationEntries WHERE EntryId = NEW.EntryId AND SessionId = NEW.SessionId))))
                BEGIN SELECT RAISE(ABORT, 'Attachment ownership or immutable source mismatch'); END;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE __tenant_attachment_rollback_guard (value INTEGER CHECK(value = 1));
                INSERT INTO __tenant_attachment_rollback_guard SELECT CASE WHEN EXISTS (SELECT 1 FROM Attachments WHERE TenantId IS NOT NULL) THEN 0 ELSE 1 END;
                DROP TABLE __tenant_attachment_rollback_guard;
                DROP TRIGGER TR_Attachments_Tenant_Insert;
                DROP TRIGGER TR_Attachments_Tenant_Update;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_Principals_UploadedByPrincipalId",
                table: "Attachments");

            migrationBuilder.DropForeignKey(
                name: "FK_Attachments_TenantSessions_TenantId_SessionId",
                table: "Attachments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_SessionId_CreatedAtUtc_AttachmentId",
                table: "Attachments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_TenantId_SessionId_UploadRequestId",
                table: "Attachments");

            migrationBuilder.DropIndex(
                name: "IX_Attachments_UploadedByPrincipalId",
                table: "Attachments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Attachments_TenantOwner",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "ContentDeletedAtUtc",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "UploadRequestId",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "UploadedByPrincipalId",
                table: "Attachments");
        }
    }
}
