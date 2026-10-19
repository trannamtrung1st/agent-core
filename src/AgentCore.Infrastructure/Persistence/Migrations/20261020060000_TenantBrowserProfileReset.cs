using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantBrowserProfileReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserProfileResetJson",
                table: "AgentInstances",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_AgentInstances_ProfileReset_Insert BEFORE INSERT ON AgentInstances
                WHEN NEW.BrowserProfileResetJson IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'Profile reset requires an existing owned Instance'); END;
                CREATE TRIGGER TR_AgentInstances_ProfileReset_Update BEFORE UPDATE ON AgentInstances
                WHEN OLD.BrowserProfileResetJson IS NOT NEW.BrowserProfileResetJson
                BEGIN
                    SELECT RAISE(ABORT, 'Invalid owned Browser profile reset') WHERE NOT COALESCE((
                        NEW.BrowserProfileResetJson IS NOT NULL AND length(CAST(NEW.BrowserProfileResetJson AS BLOB)) <= 4096
                        AND json_valid(NEW.BrowserProfileResetJson) AND json_type(NEW.BrowserProfileResetJson) = 'object'
                        AND (SELECT count(*) FROM json_each(NEW.BrowserProfileResetJson)) = 8
                        AND (SELECT count(DISTINCT key) FROM json_each(NEW.BrowserProfileResetJson)
                            WHERE key IN ('operationId','principalId','providerEpoch','witness','status','reasonCode','requestedAt','recordedAt')) = 8
                        AND json_type(NEW.BrowserProfileResetJson, '$.operationId') = 'text'
                        AND length(json_extract(NEW.BrowserProfileResetJson, '$.operationId')) = 36
                        AND json_extract(NEW.BrowserProfileResetJson, '$.operationId') NOT GLOB '*[^0-9a-f-]*'
                        AND length(replace(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),'-','')) = 32
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),9,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),14,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),19,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.operationId'),24,1) = '-'
                        AND json_extract(NEW.BrowserProfileResetJson, '$.operationId') <> '00000000-0000-0000-0000-000000000000'
                        AND json_type(NEW.BrowserProfileResetJson, '$.providerEpoch') = 'text'
                        AND length(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch')) = 36
                        AND json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch') NOT GLOB '*[^0-9a-f-]*'
                        AND length(replace(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),'-','')) = 32
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),9,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),14,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),19,1) = '-'
                        AND substr(json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch'),24,1) = '-'
                        AND json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch') <> '00000000-0000-0000-0000-000000000000'
                        AND json_type(NEW.BrowserProfileResetJson, '$.witness') = 'text'
                        AND length(json_extract(NEW.BrowserProfileResetJson, '$.witness')) = 64
                        AND json_extract(NEW.BrowserProfileResetJson, '$.witness') NOT GLOB '*[^0-9a-f]*'
                        AND json_type(NEW.BrowserProfileResetJson, '$.requestedAt') = 'text'
                        AND julianday(json_extract(NEW.BrowserProfileResetJson, '$.requestedAt')) IS NOT NULL
                        AND NEW.Revision = OLD.Revision + 1
                        AND EXISTS (SELECT 1 FROM SystemIdentities i JOIN TenantUsers u ON u.TenantId = i.TenantId
                            WHERE lower(i.AgentInstanceId) = lower(NEW.InstanceId) AND i.Kind = 0 AND i.TenantId IS NOT NULL
                                AND lower(u.PrincipalId) = json_extract(NEW.BrowserProfileResetJson, '$.principalId'))
                        AND ((json_extract(NEW.BrowserProfileResetJson, '$.status') = 'pending'
                                AND json_extract(NEW.BrowserProfileResetJson, '$.reasonCode') = 'browser_profile_reset_started'
                                AND json_type(NEW.BrowserProfileResetJson, '$.recordedAt') = 'null'
                                AND (OLD.BrowserProfileResetJson IS NULL OR json_extract(OLD.BrowserProfileResetJson, '$.status') IN ('reset','unknown'))
                                AND (OLD.BrowserProfileResetJson IS NULL OR json_extract(OLD.BrowserProfileResetJson, '$.operationId') IS NOT json_extract(NEW.BrowserProfileResetJson, '$.operationId')))
                            OR (OLD.BrowserProfileResetJson IS NOT NULL AND json_extract(OLD.BrowserProfileResetJson, '$.status') = 'pending'
                                AND json_extract(NEW.BrowserProfileResetJson, '$.status') IN ('reset','unknown')
                                AND json_extract(NEW.BrowserProfileResetJson, '$.reasonCode') = CASE json_extract(NEW.BrowserProfileResetJson, '$.status')
                                    WHEN 'reset' THEN 'browser_profile_reset_completed' ELSE 'browser_profile_reset_unknown' END
                                AND json_type(NEW.BrowserProfileResetJson, '$.recordedAt') = 'text'
                                AND julianday(json_extract(NEW.BrowserProfileResetJson, '$.recordedAt')) >= julianday(json_extract(NEW.BrowserProfileResetJson, '$.requestedAt'))
                                AND json_extract(OLD.BrowserProfileResetJson, '$.operationId') IS json_extract(NEW.BrowserProfileResetJson, '$.operationId')
                                AND json_extract(OLD.BrowserProfileResetJson, '$.principalId') IS json_extract(NEW.BrowserProfileResetJson, '$.principalId')
                                AND json_extract(OLD.BrowserProfileResetJson, '$.providerEpoch') IS json_extract(NEW.BrowserProfileResetJson, '$.providerEpoch')
                                AND json_extract(OLD.BrowserProfileResetJson, '$.witness') IS json_extract(NEW.BrowserProfileResetJson, '$.witness')
                                AND json_extract(OLD.BrowserProfileResetJson, '$.requestedAt') IS json_extract(NEW.BrowserProfileResetJson, '$.requestedAt')))
                    ), 0);
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ProfileResetRollbackGuard (OwnedRows INTEGER CHECK (OwnedRows = 0));
                INSERT INTO ProfileResetRollbackGuard SELECT count(*) FROM AgentInstances WHERE BrowserProfileResetJson IS NOT NULL;
                DROP TABLE ProfileResetRollbackGuard;
                DROP TRIGGER TR_AgentInstances_ProfileReset_Update;
                DROP TRIGGER TR_AgentInstances_ProfileReset_Insert;
                ALTER TABLE AgentInstances DROP COLUMN BrowserProfileResetJson;
                """);
        }
    }
}
