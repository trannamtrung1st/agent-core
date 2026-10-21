using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantUserProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantUserProfiles",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PreferencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantUserProfiles", x => new { x.TenantId, x.PrincipalId });
                    table.CheckConstraint("CK_TenantUserProfiles_Preferences", "length(CAST(PreferencesJson AS BLOB)) <= 4096 AND CASE WHEN json_valid(PreferencesJson) THEN json_type(PreferencesJson) = 'object' ELSE 0 END");
                    table.CheckConstraint("CK_TenantUserProfiles_Revision", "Revision > 0 AND UpdatedAtUtc >= 0");
                    table.ForeignKey(
                        name: "FK_TenantUserProfiles_TenantUsers_TenantId_PrincipalId_AccountId",
                        columns: x => new { x.TenantId, x.PrincipalId, x.AccountId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId", "AccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantUserProfiles_TenantId_PrincipalId_AccountId",
                table: "TenantUserProfiles",
                columns: new[] { "TenantId", "PrincipalId", "AccountId" });
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_TenantUserProfiles_Owner_Update BEFORE UPDATE ON TenantUserProfiles
                WHEN OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId
                    OR NEW.Revision <> OLD.Revision + 1 OR NEW.UpdatedAtUtc < OLD.UpdatedAtUtc
                BEGIN SELECT RAISE(ABORT, 'User Profile ownership and revision are immutable'); END;
                CREATE TRIGGER TR_TenantUsers_ProfileOwner_Update BEFORE UPDATE ON TenantUsers
                WHEN (OLD.UserId IS NOT NEW.UserId OR OLD.TenantId IS NOT NEW.TenantId OR OLD.PrincipalId IS NOT NEW.PrincipalId OR OLD.AccountId IS NOT NEW.AccountId)
                    AND EXISTS (SELECT 1 FROM TenantUserProfiles p WHERE p.TenantId = OLD.TenantId AND p.PrincipalId = OLD.PrincipalId AND p.AccountId = OLD.AccountId)
                BEGIN SELECT RAISE(ABORT, 'Retain the actual User Profile membership owner'); END;
                CREATE TRIGGER TR_TenantUserProfiles_Revision_Insert BEFORE INSERT ON TenantUserProfiles
                WHEN NEW.Revision <> 1
                BEGIN SELECT RAISE(ABORT, 'User Profile starts at revision one'); END;
                """);
            migrationBuilder.Sql(InsertShape);
            migrationBuilder.Sql(UpdateShape);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE TenantUserProfileDowngradeGuard (RetainedRows INTEGER CHECK (RetainedRows = 0));
                INSERT INTO TenantUserProfileDowngradeGuard SELECT count(*) FROM TenantUserProfiles;
                DROP TABLE TenantUserProfileDowngradeGuard;
                """);
            migrationBuilder.Sql("DROP TRIGGER TR_TenantUsers_ProfileOwner_Update;");
            migrationBuilder.DropTable(name: "TenantUserProfiles");
        }

        // Frozen migration-local guard; no live validation helper can alter historical DDL.
        private const string ValidShape = """
            json_valid(NEW.PreferencesJson) AND json_type(NEW.PreferencesJson) = 'object'
            AND length(CAST(NEW.PreferencesJson AS BLOB)) <= 4096
            AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) <= 4
            AND (SELECT count(*) FROM json_each(NEW.PreferencesJson)) = (SELECT count(DISTINCT key) FROM json_each(NEW.PreferencesJson))
            AND NOT EXISTS (SELECT 1 FROM json_each(NEW.PreferencesJson) p WHERE
                p.key NOT IN ('preferredName','language','locale','timeZone') OR p.type <> 'object'
                OR (SELECT count(*) FROM json_each(p.value)) <> 3
                OR (SELECT count(DISTINCT key) FROM json_each(p.value) WHERE key IN ('value','source','updatedAt')) <> 3
                OR json_type(p.value, '$.value') IS NOT 'text' OR length(trim(json_extract(p.value, '$.value'))) = 0
                OR length(json_extract(p.value, '$.value')) > CASE p.key WHEN 'preferredName' THEN 128 WHEN 'language' THEN 32 WHEN 'locale' THEN 35 ELSE 64 END
                OR json_type(p.value, '$.source') IS NOT 'integer' OR json_extract(p.value, '$.source') <> 0
                OR json_type(p.value, '$.updatedAt') IS NOT 'text' OR julianday(json_extract(p.value, '$.updatedAt')) IS NULL
                OR NOT (substr(json_extract(p.value, '$.updatedAt'), -1) = 'Z' OR substr(json_extract(p.value, '$.updatedAt'), -6) = '+00:00')
                OR julianday(json_extract(p.value, '$.updatedAt')) > julianday(NEW.UpdatedAtUtc / 1000.0, 'unixepoch'))
            """;
        private const string InsertShape = "CREATE TRIGGER TR_TenantUserProfiles_Shape_Insert BEFORE INSERT ON TenantUserProfiles BEGIN SELECT RAISE(ABORT, 'Invalid owned User Profile') WHERE NOT COALESCE((" + ValidShape + "), 0); END;";
        private const string UpdateShape = "CREATE TRIGGER TR_TenantUserProfiles_Shape_Update BEFORE UPDATE ON TenantUserProfiles BEGIN SELECT RAISE(ABORT, 'Invalid owned User Profile') WHERE NOT COALESCE((" + ValidShape + "), 0); END;";
    }
}
