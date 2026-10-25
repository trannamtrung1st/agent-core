using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionSpaceContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "EnvironmentSelectionEpoch",
                table: "Spaces",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ContextSelectionJson",
                table: "Sessions",
                type: "TEXT",
                nullable: true);
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Spaces_EnvironmentEpoch_INSERT BEFORE INSERT ON Spaces WHEN NEW.EnvironmentSelectionEpoch<>0
                BEGIN SELECT RAISE(ABORT, 'A Space starts with the initial Environment epoch'); END;
                CREATE TRIGGER TR_Spaces_EnvironmentEpoch_UPDATE BEFORE UPDATE ON Spaces
                WHEN NEW.EnvironmentSelectionEpoch<>OLD.EnvironmentSelectionEpoch+CASE WHEN OLD.EnvironmentsEnabled=1 AND NEW.EnvironmentsEnabled=0 THEN 1 ELSE 0 END
                BEGIN SELECT RAISE(ABORT, 'Environment epoch must track disable transitions'); END;
                CREATE TRIGGER TR_Sessions_ContextShape_INSERT BEFORE INSERT ON Sessions
                WHEN NEW.ContextSelectionJson IS NOT NULL AND NOT COALESCE(length(CAST(NEW.ContextSelectionJson AS BLOB))<=4096 AND json_valid(NEW.ContextSelectionJson)
                  AND json_type(NEW.ContextSelectionJson)='object' AND json_type(NEW.ContextSelectionJson,'$.tenantId')='text'
                  AND json_type(NEW.ContextSelectionJson,'$.spaceId')='text' AND json_extract(NEW.ContextSelectionJson,'$.revision')>=1
                  AND json_extract(NEW.ContextSelectionJson,'$.environmentSelectionEpoch')>=0,0)
                BEGIN SELECT RAISE(ABORT, 'Invalid Session context'); END;
                CREATE TRIGGER TR_Sessions_ContextShape_UPDATE BEFORE UPDATE ON Sessions
                WHEN NEW.ContextSelectionJson IS NOT OLD.ContextSelectionJson AND NOT COALESCE(length(CAST(NEW.ContextSelectionJson AS BLOB))<=4096 AND json_valid(NEW.ContextSelectionJson)
                  AND json_type(NEW.ContextSelectionJson)='object' AND json_type(NEW.ContextSelectionJson,'$.tenantId')='text'
                  AND json_type(NEW.ContextSelectionJson,'$.spaceId')='text' AND json_extract(NEW.ContextSelectionJson,'$.revision')>=1
                  AND json_extract(NEW.ContextSelectionJson,'$.environmentSelectionEpoch')>=0,0)
                BEGIN SELECT RAISE(ABORT, 'Invalid Session context'); END;
                CREATE TRIGGER TR_TenantSessions_Space_INSERT BEFORE INSERT ON TenantSessions
                WHEN NOT EXISTS(SELECT 1 FROM Sessions s JOIN Spaces p ON p.TenantId=NEW.TenantId AND lower(p.SpaceId)=lower(json_extract(s.ContextSelectionJson,'$.spaceId'))
                  JOIN SpaceMemberships m ON m.TenantId=p.TenantId AND m.SpaceId=p.SpaceId AND m.InstanceId=NEW.AgentInstanceId
                  WHERE s.SessionId=NEW.SessionId AND lower(json_extract(s.ContextSelectionJson,'$.tenantId'))=lower(NEW.TenantId) AND p.DeletedAt IS NULL
                  AND (p.Kind=1 OR p.OwnerInstanceId=NEW.AgentInstanceId)
                  AND (json_extract(s.ContextSelectionJson,'$.environmentId') IS NULL OR (p.EnvironmentsEnabled=1 AND json_extract(s.ContextSelectionJson,'$.environmentSelectionEpoch')=p.EnvironmentSelectionEpoch
                    AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=p.TenantId AND e.SpaceId=p.SpaceId AND lower(e.EnvironmentId)=lower(json_extract(s.ContextSelectionJson,'$.environmentId')) AND e.DeletedAt IS NULL))))
                BEGIN SELECT RAISE(ABORT, 'Tenant Session requires its exact Space membership and profile'); END;
                CREATE TRIGGER TR_Sessions_Space_UPDATE BEFORE UPDATE ON Sessions WHEN NEW.ContextSelectionJson IS NOT OLD.ContextSelectionJson
                  AND EXISTS(SELECT 1 FROM TenantSessions t WHERE t.SessionId=NEW.SessionId)
                  AND NOT EXISTS(SELECT 1 FROM TenantSessions t JOIN Spaces p ON p.TenantId=t.TenantId AND lower(p.SpaceId)=lower(json_extract(NEW.ContextSelectionJson,'$.spaceId'))
                    JOIN SpaceMemberships m ON m.TenantId=p.TenantId AND m.SpaceId=p.SpaceId AND m.InstanceId=t.AgentInstanceId
                    WHERE t.SessionId=NEW.SessionId AND lower(json_extract(NEW.ContextSelectionJson,'$.tenantId'))=lower(t.TenantId) AND p.DeletedAt IS NULL
                    AND (p.Kind=1 OR p.OwnerInstanceId=t.AgentInstanceId)
                    AND (json_extract(NEW.ContextSelectionJson,'$.environmentId') IS NULL OR (p.EnvironmentsEnabled=1 AND json_extract(NEW.ContextSelectionJson,'$.environmentSelectionEpoch')=p.EnvironmentSelectionEpoch
                      AND EXISTS(SELECT 1 FROM SpaceEnvironments e WHERE e.TenantId=p.TenantId AND e.SpaceId=p.SpaceId AND lower(e.EnvironmentId)=lower(json_extract(NEW.ContextSelectionJson,'$.environmentId')) AND e.DeletedAt IS NULL))))
                BEGIN SELECT RAISE(ABORT, 'Session context must keep its exact tenant membership'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ContextDowngradeGuard (Rows INTEGER CHECK(Rows=0));
                INSERT INTO ContextDowngradeGuard SELECT (SELECT count(*) FROM Sessions WHERE ContextSelectionJson IS NOT NULL)+(SELECT count(*) FROM Spaces WHERE EnvironmentSelectionEpoch<>0);
                DROP TABLE ContextDowngradeGuard;
                DROP TRIGGER TR_Spaces_EnvironmentEpoch_INSERT;
                DROP TRIGGER TR_Spaces_EnvironmentEpoch_UPDATE;
                DROP TRIGGER TR_Sessions_ContextShape_INSERT;
                DROP TRIGGER TR_Sessions_ContextShape_UPDATE;
                DROP TRIGGER TR_TenantSessions_Space_INSERT;
                DROP TRIGGER TR_Sessions_Space_UPDATE;
                ALTER TABLE Spaces DROP COLUMN EnvironmentSelectionEpoch;
                ALTER TABLE Sessions DROP COLUMN ContextSelectionJson;
                """);
        }
    }
}
