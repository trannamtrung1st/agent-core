using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpaceEnvironmentsAndSecrets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "Spaces",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SpaceEnvironments",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceEnvironments", x => new { x.TenantId, x.SpaceId, x.EnvironmentId });
                    table.CheckConstraint("CK_SpaceEnvironments_Values", "Revision>=1 AND length(Name) BETWEEN 1 AND 120 AND length(Description)<=2048 AND length(NormalizedName) BETWEEN 1 AND 240");
                    table.ForeignKey(
                        name: "FK_SpaceEnvironments_Spaces_TenantId_SpaceId",
                        columns: x => new { x.TenantId, x.SpaceId },
                        principalTable: "Spaces",
                        principalColumns: new[] { "TenantId", "SpaceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantSecrets",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecretId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSecrets", x => new { x.TenantId, x.SecretId });
                    table.CheckConstraint("CK_TenantSecrets_Values", "Status IN (0,1,2) AND Revision>=1 AND ProtectionVersion>=1 AND length(Name) BETWEEN 1 AND 120 AND length(Description)<=2048 AND ((Status=2 AND ProtectedPayload='') OR (Status<>2 AND length(ProtectedPayload) BETWEEN 1 AND 131072))");
                    table.ForeignKey(
                        name: "FK_TenantSecrets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentVariables",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnvironmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    PlainValue = table.Column<string>(type: "TEXT", nullable: true),
                    SecretId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentVariables", x => new { x.TenantId, x.EnvironmentId, x.Name });
                    table.CheckConstraint("CK_EnvironmentVariables_Values", "Revision>=1 AND length(Name) BETWEEN 1 AND 64 AND Name=upper(Name) AND Name NOT GLOB '*[^A-Z0-9_]*' AND substr(Name,1,1) GLOB '[A-Z_]' AND length(Description)<=1024 AND ((Kind=0 AND PlainValue IS NOT NULL AND length(CAST(PlainValue AS BLOB))<=4096 AND SecretId IS NULL AND CredentialId IS NULL) OR (Kind=1 AND PlainValue IS NULL AND SecretId IS NOT NULL AND CredentialId IS NULL) OR (Kind=2 AND PlainValue IS NULL AND SecretId IS NULL AND CredentialId IS NOT NULL))");
                    table.ForeignKey(
                        name: "FK_EnvironmentVariables_SpaceEnvironments_TenantId_SpaceId_EnvironmentId",
                        columns: x => new { x.TenantId, x.SpaceId, x.EnvironmentId },
                        principalTable: "SpaceEnvironments",
                        principalColumns: new[] { "TenantId", "SpaceId", "EnvironmentId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvironmentVariables_TenantCredentials_TenantId_CredentialId",
                        columns: x => new { x.TenantId, x.CredentialId },
                        principalTable: "TenantCredentials",
                        principalColumns: new[] { "TenantId", "CredentialId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvironmentVariables_TenantSecrets_TenantId_SecretId",
                        columns: x => new { x.TenantId, x.SecretId },
                        principalTable: "TenantSecrets",
                        principalColumns: new[] { "TenantId", "SecretId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentVariables_TenantId_CredentialId",
                table: "EnvironmentVariables",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentVariables_TenantId_SecretId",
                table: "EnvironmentVariables",
                columns: new[] { "TenantId", "SecretId" });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentVariables_TenantId_SpaceId_EnvironmentId",
                table: "EnvironmentVariables",
                columns: new[] { "TenantId", "SpaceId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_SpaceEnvironments_TenantId_SpaceId_NormalizedName",
                table: "SpaceEnvironments",
                columns: new[] { "TenantId", "SpaceId", "NormalizedName" },
                unique: true,
                filter: "DeletedAt IS NULL");
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Spaces_Deleted_UPDATE BEFORE UPDATE ON Spaces WHEN OLD.DeletedAt IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'A retired Space cannot be revived'); END;
                CREATE TRIGGER TR_Environments_Owner_UPDATE BEFORE UPDATE ON SpaceEnvironments
                WHEN NEW.TenantId<>OLD.TenantId OR NEW.SpaceId<>OLD.SpaceId OR NEW.EnvironmentId<>OLD.EnvironmentId OR NEW.CreatedAt<>OLD.CreatedAt OR OLD.DeletedAt IS NOT NULL
                BEGIN SELECT RAISE(ABORT, 'Environment ownership is immutable'); END;
                CREATE TRIGGER TR_Variables_Owner_UPDATE BEFORE UPDATE ON EnvironmentVariables
                WHEN NEW.TenantId<>OLD.TenantId OR NEW.SpaceId<>OLD.SpaceId OR NEW.EnvironmentId<>OLD.EnvironmentId OR NEW.Name<>OLD.Name OR NEW.CreatedAt<>OLD.CreatedAt
                BEGIN SELECT RAISE(ABORT, 'Variable ownership is immutable'); END;
                CREATE TRIGGER TR_Secrets_Owner_UPDATE BEFORE UPDATE ON TenantSecrets
                WHEN NEW.TenantId<>OLD.TenantId OR NEW.SecretId<>OLD.SecretId OR NEW.CreatedAt<>OLD.CreatedAt OR OLD.Status=2
                BEGIN SELECT RAISE(ABORT, 'Secret ownership is immutable'); END;
                CREATE TRIGGER TR_Secrets_Referenced_UPDATE BEFORE UPDATE ON TenantSecrets WHEN NEW.Status=2
                AND (EXISTS(SELECT 1 FROM EnvironmentVariables v WHERE v.TenantId=OLD.TenantId AND v.SecretId=OLD.SecretId)
                  OR EXISTS(SELECT 1 FROM SpaceMembershipAddedBindings b WHERE b.TenantId=OLD.TenantId AND b.Kind=4 AND b.ResourceKey=lower(OLD.SecretId)))
                BEGIN SELECT RAISE(ABORT, 'Secret still has references'); END;
                CREATE TRIGGER TR_Environments_Limit_INSERT BEFORE INSERT ON SpaceEnvironments WHEN NEW.DeletedAt IS NULL
                AND (SELECT count(*) FROM SpaceEnvironments e WHERE e.TenantId=NEW.TenantId AND e.SpaceId=NEW.SpaceId AND e.DeletedAt IS NULL)>=64
                BEGIN SELECT RAISE(ABORT, 'Environment limit reached'); END;
                CREATE TRIGGER TR_Variables_Limit_INSERT BEFORE INSERT ON EnvironmentVariables
                WHEN (SELECT count(*) FROM EnvironmentVariables v WHERE v.TenantId=NEW.TenantId AND v.EnvironmentId=NEW.EnvironmentId)>=256
                BEGIN SELECT RAISE(ABORT, 'Variable limit reached'); END;
                CREATE TRIGGER TR_SpaceBindings_Protected_INSERT BEFORE INSERT ON SpaceMembershipAddedBindings
                WHEN (NEW.Kind=4 AND NOT EXISTS(SELECT 1 FROM TenantSecrets s WHERE s.TenantId=NEW.TenantId AND lower(s.SecretId)=NEW.ResourceKey AND s.Status=0))
                 OR (NEW.Kind=3 AND NOT EXISTS(SELECT 1 FROM TenantCredentials c JOIN Credentials p ON p.CredentialId=c.CredentialId WHERE c.TenantId=NEW.TenantId AND c.CredentialId=NEW.ResourceKey AND c.DeletedAt IS NULL AND p.Status='Active'))
                BEGIN SELECT RAISE(ABORT, 'Protected resource binding is unavailable'); END;
                CREATE TRIGGER TR_SpaceBindings_Protected_UPDATE BEFORE UPDATE ON SpaceMembershipAddedBindings
                BEGIN SELECT RAISE(ABORT, 'Replace a binding in its Membership transaction'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE EnvironmentDowngradeGuard (Rows INTEGER CHECK(Rows=0));
                INSERT INTO EnvironmentDowngradeGuard SELECT (SELECT count(*) FROM SpaceEnvironments)+(SELECT count(*) FROM TenantSecrets)+(SELECT count(*) FROM Spaces);
                DROP TABLE EnvironmentDowngradeGuard;
                DROP TRIGGER TR_Spaces_Deleted_UPDATE;
                DROP TRIGGER TR_Environments_Owner_UPDATE;
                DROP TRIGGER TR_Variables_Owner_UPDATE;
                DROP TRIGGER TR_Secrets_Owner_UPDATE;
                DROP TRIGGER TR_Secrets_Referenced_UPDATE;
                DROP TRIGGER TR_Environments_Limit_INSERT;
                DROP TRIGGER TR_Variables_Limit_INSERT;
                DROP TRIGGER TR_SpaceBindings_Protected_INSERT;
                DROP TRIGGER TR_SpaceBindings_Protected_UPDATE;
                """);
            migrationBuilder.DropTable(
                name: "EnvironmentVariables");

            migrationBuilder.DropTable(
                name: "SpaceEnvironments");

            migrationBuilder.DropTable(
                name: "TenantSecrets");

            // Native SQLite DROP COLUMN retains the predecessor table's ownership triggers.
            migrationBuilder.Sql("ALTER TABLE Spaces DROP COLUMN DeletedAt;");
        }
    }
}
