using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20261020040000_TenantBrowserArtifactDrops")]
public sealed class TenantBrowserArtifactDrops : Migration
{
    private const string RemoveGuards = "DROP TRIGGER TR_TenantApprovals_BrowserFiles_Update; DROP TRIGGER TR_TenantApprovals_BrowserFiles_Insert;";
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(RemoveGuards);
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Insert BEFORE INSERT ON TenantApprovalRequests
            WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop' AND
                    json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS NOT NULL)
                OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
                OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'Browser file transfer requires exact owned Artifact sources') WHERE NOT COALESCE((
                    NEW.Kind = 0 AND NEW.Permission = 'browser.use' AND NEW.ResourceType = 'agent' AND NEW.ResourceId = NEW.InstanceId
                    AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IN ('browser.upload','browser.drop')
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
                    AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Credential') IS NULL
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS 'text'
                    AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')) BETWEEN 1 AND 2048
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS 'text'
                    AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') AS BLOB)) BETWEEN 1 AND 16384
                    AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
                    AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) IS 'array'
                    AND ((json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) BETWEEN 1 AND 8
                        AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) =
                            json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds'))
                        OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop'
                            AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) = 1
                            AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') IS 'text'
                            AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.text') IS NULL
                            AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.mimeType') IS NULL))
                    AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) source
                        WHERE json_type(source.value) IS NOT 'object'
                            OR json_type(source.value, '$.ArtifactId') IS NOT 'text'
                            OR json_type(source.value, '$.DisplayName') IS NOT 'text'
                            OR json_type(source.value, '$.ContentType') IS NOT 'text'
                            OR json_type(source.value, '$.ByteSize') IS NOT 'integer'
                            OR json_type(source.value, '$.Sha256Hex') IS NOT 'text'
                            OR (SELECT count(*) FROM json_each(source.value)) <> 5
                            OR (SELECT count(DISTINCT key) FROM json_each(source.value)
                                WHERE key IN ('ArtifactId','DisplayName','ContentType','ByteSize','Sha256Hex')) <> 5
                            OR json_extract(source.value, '$.ArtifactId') IS NOT
                                CASE WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                                    THEN json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds[' || source.key || ']')
                                    ELSE json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactId') END
                            OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = NEW.TenantId AND a.SessionId = NEW.SessionId
                                AND a.ArtifactId = json_extract(source.value, '$.ArtifactId') AND a.DisplayName = json_extract(source.value, '$.DisplayName')
                                AND a.ContentType = json_extract(source.value, '$.ContentType') AND a.ByteSize = json_extract(source.value, '$.ByteSize')
                                AND a.Sha256Hex = json_extract(source.value, '$.Sha256Hex')
                                AND EXISTS (SELECT 1 FROM Sessions saved WHERE saved.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)))
                    AND (SELECT count(DISTINCT json_extract(value, '$.ArtifactId'))
                        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) =
                        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
                    AND (SELECT sum(json_extract(value, '$.ByteSize'))
                        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) <= 52428800
                ), 0);
            END;
            CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Update BEFORE UPDATE ON TenantApprovalRequests
            WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT
                json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')
                OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT
                    json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')
                OR ((json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
                    OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL)
                    AND (json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName')
                        OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson') IS NOT json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson')))
            BEGIN
                SELECT RAISE(ABORT, 'Browser file transfer sources are immutable');
            END;

            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE BrowserDropRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows = 0));
            INSERT INTO BrowserDropRollbackGuard SELECT count(*) FROM TenantApprovalRequests
                WHERE json_extract(PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.drop'
                    AND json_extract(PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL;
            DROP TABLE BrowserDropRollbackGuard;
            """);
        migrationBuilder.Sql(RemoveGuards);
        // Restore the preceding upload-only contract verbatim, without discarding retained drop authority.
        migrationBuilder.Sql("""
            CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Insert BEFORE INSERT ON TenantApprovalRequests
            WHEN json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT NULL
                OR json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT NULL
            BEGIN
                SELECT RAISE(ABORT, 'Browser upload requires exact owned Artifact sources') WHERE NOT COALESCE((
                    NEW.Kind = 0 AND NEW.Permission = 'browser.use' AND NEW.ResourceType = 'agent' AND NEW.ResourceId = NEW.InstanceId
                    AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ReadOnly') IS 'false'
                    AND json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.Credential') IS NULL
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS 'text'
                    AND length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')) BETWEEN 1 AND 2048
                    AND json_type(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS 'text'
                    AND length(CAST(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') AS BLOB)) BETWEEN 1 AND 16384
                    AND json_valid(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
                    AND json_type(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) IS 'array'
                    AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) BETWEEN 1 AND 8
                    AND json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) =
                        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds')
                    AND NOT EXISTS (SELECT 1 FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')) source
                        WHERE json_type(source.value) IS NOT 'object'
                            OR json_type(source.value, '$.ArtifactId') IS NOT 'text'
                            OR json_type(source.value, '$.DisplayName') IS NOT 'text'
                            OR json_type(source.value, '$.ContentType') IS NOT 'text'
                            OR json_type(source.value, '$.ByteSize') IS NOT 'integer'
                            OR json_type(source.value, '$.Sha256Hex') IS NOT 'text'
                            OR (SELECT count(*) FROM json_each(source.value)) <> 5
                            OR (SELECT count(DISTINCT key) FROM json_each(source.value)
                                WHERE key IN ('ArtifactId','DisplayName','ContentType','ByteSize','Sha256Hex')) <> 5
                            OR json_extract(source.value, '$.ArtifactId') IS NOT
                                json_extract(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArgumentsJson'), '$.artifactIds[' || source.key || ']')
                            OR NOT EXISTS (SELECT 1 FROM Artifacts a WHERE a.TenantId = NEW.TenantId AND a.SessionId = NEW.SessionId
                                AND a.ArtifactId = json_extract(source.value, '$.ArtifactId') AND a.DisplayName = json_extract(source.value, '$.DisplayName')
                                AND a.ContentType = json_extract(source.value, '$.ContentType') AND a.ByteSize = json_extract(source.value, '$.ByteSize')
                                AND a.Sha256Hex = json_extract(source.value, '$.Sha256Hex')
                                AND EXISTS (SELECT 1 FROM Sessions saved WHERE saved.SessionId = a.SessionId AND saved.DurablyDeletedAtUtc IS NULL)))
                    AND (SELECT count(DISTINCT json_extract(value, '$.ArtifactId'))
                        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) =
                        json_array_length(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))
                    AND (SELECT sum(json_extract(value, '$.ByteSize'))
                        FROM json_each(json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson'))) <= 52428800
                ), 0);
            END;
            CREATE TRIGGER TR_TenantApprovals_BrowserFiles_Update BEFORE UPDATE ON TenantApprovalRequests
            WHEN json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson') IS NOT
                json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ArtifactSourcesJson')
                OR json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin') IS NOT
                    json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.FileInputOrigin')
                OR (json_extract(NEW.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS 'browser.upload'
                    AND json_extract(OLD.PayloadJson, '$.Operation.BrowserCommand.ToolName') IS NOT 'browser.upload')
            BEGIN
                SELECT RAISE(ABORT, 'Browser upload sources are immutable');
            END;

            """);
    }
}
