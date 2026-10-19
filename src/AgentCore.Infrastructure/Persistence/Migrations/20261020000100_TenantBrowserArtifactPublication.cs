using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantBrowserArtifactPublication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserArtifactsJson",
                table: "TenantOperationReceipts",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Insert BEFORE INSERT ON TenantApprovalRequests
                WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT NULL
                    OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.screenshot'
                BEGIN
                    SELECT RAISE(ABORT, 'Browser publication requires exact bounded intent') WHERE NOT COALESCE((
                        NEW.Permission IN ('browser.use','credentials.use')
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object'
                        AND (SELECT count(*) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')) = 8
                        AND (SELECT count(DISTINCT key) FROM json_each(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
                            WHERE key IN ('Capture','Downloads','CaptureBytes','DownloadBytes','CapturesPerScope','DownloadsPerScope','CapturePrivacyMode','CapturePrivacyHash')) = 8
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') IN ('true','false')
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') IN ('true','false')
                        AND (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
                            OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1)
                        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') =
                            (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') = 'browser.screenshot')
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') IS 'integer'
                        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes') BETWEEN 1 AND 1500000
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') IS 'integer'
                        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes') BETWEEN 1 AND 5242880
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') IS 'integer'
                        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturesPerScope') BETWEEN 1 AND 4
                        AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') IS 'integer'
                        AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadsPerScope') BETWEEN 1 AND 2
                        AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
                            AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IN ('Protected','Unmasked')
                            AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS 'text'
                            AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash')) = 64
                            AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') NOT GLOB '*[^0-9a-f]*')
                            OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 0
                                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyMode') IS NULL
                                AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CapturePrivacyHash') IS NULL))
                    ),0);
                END;
                CREATE TRIGGER TR_TenantApprovals_BrowserArtifacts_Update BEFORE UPDATE ON TenantApprovalRequests
                WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT
                    json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Artifacts')
                BEGIN SELECT RAISE(ABORT, 'Browser publication intent is immutable'); END;

                CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Insert BEFORE INSERT ON TenantOperationReceipts
                WHEN NEW.BrowserArtifactsJson IS NOT NULL
                BEGIN
                    SELECT RAISE(ABORT, 'Browser Artifact receipt requires actual owned publication') WHERE NOT COALESCE((
                        NEW.RequestId IS NOT NULL AND length(CAST(NEW.BrowserArtifactsJson AS BLOB)) <= 4096
                        AND json_valid(NEW.BrowserArtifactsJson) AND json_type(NEW.BrowserArtifactsJson) IS 'object'
                        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson)) = 3
                        AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserArtifactsJson) WHERE key IN ('status','reasonCode','files')) = 3
                        AND json_extract(NEW.BrowserArtifactsJson, '$.status') IN ('published','empty','unavailable')
                        AND (json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL OR json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IN
                            ('browser_command_not_completed','artifact_publication_unavailable','permission_denied','artifact_quota_exceeded','capture_limit','download_limit','capture_invalid','download_invalid'))
                        AND json_type(NEW.BrowserArtifactsJson, '$.files') IS 'array'
                        AND json_array_length(NEW.BrowserArtifactsJson, '$.files') <= 3
                        AND ((json_extract(NEW.BrowserArtifactsJson, '$.status') = 'published' AND NEW.Outcome = 0
                            AND json_array_length(NEW.BrowserArtifactsJson, '$.files') > 0)
                            OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'empty' AND NEW.Outcome = 0
                                AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NULL)
                            OR (json_extract(NEW.BrowserArtifactsJson, '$.status') = 'unavailable'
                                AND json_array_length(NEW.BrowserArtifactsJson, '$.files') = 0 AND json_extract(NEW.BrowserArtifactsJson, '$.reasonCode') IS NOT NULL))
                        AND EXISTS (SELECT 1 FROM TenantApprovalRequests request WHERE request.RequestId = NEW.RequestId
                            AND json_type(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS 'object')
                        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'capture') <= 1
                        AND (SELECT count(*) FROM json_each(NEW.BrowserArtifactsJson, '$.files') WHERE json_extract(value, '$.kind') = 'download') <= 2
                        AND (SELECT count(DISTINCT json_extract(value, '$.artifactId')) FROM json_each(NEW.BrowserArtifactsJson, '$.files')) = json_array_length(NEW.BrowserArtifactsJson, '$.files')
                        AND NOT EXISTS (SELECT 1 FROM json_each(NEW.BrowserArtifactsJson, '$.files') file
                            WHERE json_type(file.value) IS NOT 'object' OR (SELECT count(*) FROM json_each(file.value)) <> 2
                                OR (SELECT count(DISTINCT key) FROM json_each(file.value) WHERE key IN ('kind','artifactId')) <> 2
                                OR json_type(file.value, '$.artifactId') IS NOT 'text' OR json_extract(file.value, '$.kind') NOT IN ('capture','download')
                                OR NOT EXISTS (SELECT 1 FROM Artifacts artifact JOIN TenantApprovalRequests request ON request.RequestId = NEW.RequestId
                                    WHERE artifact.TenantId = NEW.TenantId AND artifact.SessionId = NEW.SessionId AND artifact.AgentRunId = NEW.RunId
                                        AND artifact.ArtifactId = json_extract(file.value, '$.artifactId')
                                        AND ((json_extract(file.value, '$.kind') = 'capture'
                                            AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Capture') = 1
                                            AND artifact.ArtifactId = lower(NEW.OperationId) AND artifact.ContentType IN ('image/png','image/jpeg','image/webp')
                                            AND artifact.ByteSize BETWEEN 1 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.CaptureBytes'))
                                            OR (json_extract(file.value, '$.kind') = 'download'
                                                AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.Downloads') = 1
                                                AND artifact.ByteSize BETWEEN 0 AND json_extract(request.PayloadJson, '$.Operation.BrowserCommand.Artifacts.DownloadBytes')))))
                    ),0);
                END;
                CREATE TRIGGER TR_TenantReceipts_BrowserArtifacts_Update BEFORE UPDATE ON TenantOperationReceipts
                WHEN OLD.BrowserArtifactsJson IS NOT NULL OR NEW.BrowserArtifactsJson IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'Browser Artifact receipts are immutable'); END;
                """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE BrowserArtifactRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows = 0));
                INSERT INTO BrowserArtifactRollbackGuard SELECT
                    (SELECT count(*) FROM TenantApprovalRequests WHERE json_extract(PayloadJson, '$.Operation.BrowserCommand.Artifacts') IS NOT NULL)
                    + (SELECT count(*) FROM TenantOperationReceipts WHERE BrowserArtifactsJson IS NOT NULL);
                DROP TABLE BrowserArtifactRollbackGuard;
                DROP TRIGGER TR_TenantApprovals_BrowserArtifacts_Insert;
                DROP TRIGGER TR_TenantApprovals_BrowserArtifacts_Update;
                DROP TRIGGER TR_TenantReceipts_BrowserArtifacts_Insert;
                DROP TRIGGER TR_TenantReceipts_BrowserArtifacts_Update;
                """);
            // The preceding trigger-only migration has no EF target model for a rebuild.
            migrationBuilder.Sql("ALTER TABLE TenantOperationReceipts DROP COLUMN BrowserArtifactsJson;");
        }
    }
}
