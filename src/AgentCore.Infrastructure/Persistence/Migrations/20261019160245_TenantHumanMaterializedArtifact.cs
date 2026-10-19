using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class TenantHumanMaterializedArtifact : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Rebuild explicitly so the subsequent owned-source triggers survive SQLite's rebuild.
        migrationBuilder.Sql("""
            CREATE TABLE ef_temp_Artifacts (
                ArtifactId TEXT NOT NULL CONSTRAINT PK_Artifacts PRIMARY KEY,
                SessionId TEXT NOT NULL, BlobKey TEXT NOT NULL, DisplayName TEXT NOT NULL,
                ContentType TEXT NOT NULL, ByteSize INTEGER NOT NULL, Sha256Hex TEXT NOT NULL,
                SourceAttachmentId TEXT NULL, AgentRunId TEXT NULL, WorkspaceLogicalPath TEXT NULL,
                CreatedAtUtc INTEGER NOT NULL, TenantId TEXT NULL, CreatedByPrincipalId TEXT NULL,
                CONSTRAINT FK_Artifacts_TenantSessions_TenantId_SessionId FOREIGN KEY (TenantId, SessionId)
                    REFERENCES TenantSessions (TenantId, SessionId) ON DELETE RESTRICT,
                CONSTRAINT CK_Artifacts_TenantOwner CHECK (TenantId IS NULL OR (((AgentRunId IS NOT NULL AND CreatedByPrincipalId IS NULL AND SourceAttachmentId IS NULL AND WorkspaceLogicalPath IS NULL) OR (AgentRunId IS NULL AND CreatedByPrincipalId IS NOT NULL AND SourceAttachmentId IS NOT NULL AND WorkspaceLogicalPath IS NOT NULL AND WorkspaceLogicalPath = '/working/' || DisplayName AND instr(DisplayName, '/') = 0 AND instr(DisplayName, char(92)) = 0)) AND ByteSize >= 0 AND length(Sha256Hex) = 64 AND BlobKey = 'tenants/' || lower(replace(TenantId, '-', '')) || '/' || lower(replace(SessionId, '-', '')) || '/' || lower(replace(ArtifactId, '-', ''))))
            );
            INSERT INTO ef_temp_Artifacts (ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc,TenantId)
            SELECT ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc,TenantId FROM Artifacts;
            DROP TABLE Artifacts;
            ALTER TABLE ef_temp_Artifacts RENAME TO Artifacts;
            CREATE INDEX IX_Artifacts_SessionId ON Artifacts (SessionId);
            CREATE INDEX IX_Artifacts_SessionId_AgentRunId ON Artifacts (SessionId, AgentRunId);
            CREATE INDEX IX_Artifacts_TenantId_SessionId_CreatedAtUtc_ArtifactId ON Artifacts (TenantId,SessionId,CreatedAtUtc,ArtifactId);
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_Artifacts_TenantRun_Insert BEFORE INSERT ON Artifacts
            WHEN NEW.TenantId IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'owned Artifact requires actual provenance')
                WHERE NOT (
                    (NEW.AgentRunId IS NOT NULL AND EXISTS (SELECT 1 FROM AgentRuns r WHERE r.TenantId = NEW.TenantId
                        AND r.AgentRunId = NEW.AgentRunId AND r.SessionId = NEW.SessionId)) OR
                    (NEW.AgentRunId IS NULL AND EXISTS (SELECT 1 FROM Principals p WHERE p.TenantId = NEW.TenantId
                        AND p.PrincipalId = NEW.CreatedByPrincipalId AND p.Kind = 0)
                        AND EXISTS (SELECT 1 FROM Attachments s WHERE s.TenantId = NEW.TenantId AND s.SessionId = NEW.SessionId
                            AND s.AttachmentId = NEW.SourceAttachmentId AND s.ByteSize = NEW.ByteSize AND s.Sha256Hex = NEW.Sha256Hex
                            AND s.ContentType = NEW.ContentType))
                );
            END;
            """);
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_Artifacts_TenantRun_Update BEFORE UPDATE ON Artifacts
            WHEN OLD.TenantId IS NOT NULL OR NEW.TenantId IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'owned Artifact is immutable');
            END;
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Human source attribution is factual ownership, and cannot be erased by downgrade.
        migrationBuilder.Sql("""
            CREATE TEMP TABLE HumanArtifactRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows = 0));
            INSERT INTO HumanArtifactRollbackGuard SELECT COUNT(*) FROM Artifacts WHERE CreatedByPrincipalId IS NOT NULL;
            DROP TABLE HumanArtifactRollbackGuard;
            """);
        migrationBuilder.Sql("""
            CREATE TABLE ef_temp_Artifacts (
                ArtifactId TEXT NOT NULL CONSTRAINT PK_Artifacts PRIMARY KEY,
                SessionId TEXT NOT NULL, BlobKey TEXT NOT NULL, DisplayName TEXT NOT NULL,
                ContentType TEXT NOT NULL, ByteSize INTEGER NOT NULL, Sha256Hex TEXT NOT NULL,
                SourceAttachmentId TEXT NULL, AgentRunId TEXT NULL, WorkspaceLogicalPath TEXT NULL,
                CreatedAtUtc INTEGER NOT NULL, TenantId TEXT NULL,
                CONSTRAINT FK_Artifacts_TenantSessions_TenantId_SessionId FOREIGN KEY (TenantId, SessionId)
                    REFERENCES TenantSessions (TenantId, SessionId) ON DELETE RESTRICT,
                CONSTRAINT CK_Artifacts_TenantOwner CHECK (TenantId IS NULL OR (AgentRunId IS NOT NULL AND SourceAttachmentId IS NULL AND WorkspaceLogicalPath IS NULL AND ByteSize >= 0 AND length(Sha256Hex) = 64 AND BlobKey = 'tenants/' || lower(replace(TenantId, '-', '')) || '/' || lower(replace(SessionId, '-', '')) || '/' || lower(replace(ArtifactId, '-', ''))))
            );
            INSERT INTO ef_temp_Artifacts (ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc,TenantId)
            SELECT ArtifactId,SessionId,BlobKey,DisplayName,ContentType,ByteSize,Sha256Hex,
                SourceAttachmentId,AgentRunId,WorkspaceLogicalPath,CreatedAtUtc,TenantId FROM Artifacts;
            DROP TABLE Artifacts;
            ALTER TABLE ef_temp_Artifacts RENAME TO Artifacts;
            CREATE INDEX IX_Artifacts_SessionId ON Artifacts (SessionId);
            CREATE INDEX IX_Artifacts_SessionId_AgentRunId ON Artifacts (SessionId, AgentRunId);
            CREATE INDEX IX_Artifacts_TenantId_SessionId_CreatedAtUtc_ArtifactId ON Artifacts (TenantId,SessionId,CreatedAtUtc,ArtifactId);
            """);
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
}
