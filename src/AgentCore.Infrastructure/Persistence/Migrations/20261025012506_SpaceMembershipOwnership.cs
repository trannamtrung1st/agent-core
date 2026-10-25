using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpaceMembershipOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A reviewed operator reset is required for pre-cutover tenant agents. Never adopt old homes at startup.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE SpaceCleanBootstrapGuard (ExistingAgents INTEGER CHECK(ExistingAgents=0));
                INSERT INTO SpaceCleanBootstrapGuard SELECT count(*) FROM SystemIdentities WHERE AgentInstanceId IS NOT NULL;
                DROP TABLE SpaceCleanBootstrapGuard;
                """);
            migrationBuilder.CreateTable(
                name: "Spaces",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: false),
                    EnvironmentsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Spaces", x => new { x.TenantId, x.SpaceId });
                    table.CheckConstraint("CK_Spaces_Owner", "Revision >= 1 AND ((Kind = 0 AND OwnerInstanceId IS NOT NULL) OR (Kind = 1 AND OwnerInstanceId IS NULL)) AND length(Name) BETWEEN 1 AND 160 AND length(Instructions) <= 16384 AND EnvironmentsEnabled IN (0,1)");
                    table.ForeignKey(
                        name: "FK_Spaces_SystemIdentities_TenantId_OwnerInstanceId",
                        columns: x => new { x.TenantId, x.OwnerInstanceId },
                        principalTable: "SystemIdentities",
                        principalColumns: new[] { "TenantId", "AgentInstanceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Spaces_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpaceMemberships",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Responsibility = table.Column<string>(type: "TEXT", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceMemberships", x => new { x.TenantId, x.SpaceId, x.InstanceId });
                    table.CheckConstraint("CK_SpaceMemberships_Values", "Revision >= 1 AND length(Instructions) <= 16384 AND length(Responsibility) <= 160");
                    table.ForeignKey(
                        name: "FK_SpaceMemberships_Spaces_TenantId_SpaceId",
                        columns: x => new { x.TenantId, x.SpaceId },
                        principalTable: "Spaces",
                        principalColumns: new[] { "TenantId", "SpaceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SpaceMemberships_SystemIdentities_TenantId_InstanceId",
                        columns: x => new { x.TenantId, x.InstanceId },
                        principalTable: "SystemIdentities",
                        principalColumns: new[] { "TenantId", "AgentInstanceId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SpaceMembershipAddedBindings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceKey = table.Column<string>(type: "TEXT", nullable: false),
                    AccountRef = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceMembershipAddedBindings", x => new { x.TenantId, x.SpaceId, x.InstanceId, x.Kind, x.ResourceKey });
                    table.CheckConstraint("CK_SpaceBindings_Kind", "Kind BETWEEN 0 AND 4 AND length(ResourceKey) BETWEEN 1 AND 256 AND ((Kind = 3 AND AccountRef IS NOT NULL) OR (Kind <> 3 AND AccountRef IS NULL))");
                    table.ForeignKey(
                        name: "FK_SpaceMembershipAddedBindings_SpaceMemberships_TenantId_SpaceId_InstanceId",
                        columns: x => new { x.TenantId, x.SpaceId, x.InstanceId },
                        principalTable: "SpaceMemberships",
                        principalColumns: new[] { "TenantId", "SpaceId", "InstanceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpaceMembershipDisabledBindings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SpaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    BindingKey = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceMembershipDisabledBindings", x => new { x.TenantId, x.SpaceId, x.InstanceId, x.BindingKey });
                    table.ForeignKey(
                        name: "FK_SpaceMembershipDisabledBindings_SpaceMemberships_TenantId_SpaceId_InstanceId",
                        columns: x => new { x.TenantId, x.SpaceId, x.InstanceId },
                        principalTable: "SpaceMemberships",
                        principalColumns: new[] { "TenantId", "SpaceId", "InstanceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SpaceMembershipAddedBindings_TenantId_SpaceId_InstanceId_AccountRef",
                table: "SpaceMembershipAddedBindings",
                columns: new[] { "TenantId", "SpaceId", "InstanceId", "AccountRef" },
                unique: true,
                filter: "AccountRef IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceMemberships_TenantId_InstanceId",
                table: "SpaceMemberships",
                columns: new[] { "TenantId", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Spaces_TenantId_OwnerInstanceId",
                table: "Spaces",
                columns: new[] { "TenantId", "OwnerInstanceId" },
                unique: true,
                filter: "OwnerInstanceId IS NOT NULL");
            migrationBuilder.Sql("""
                CREATE TRIGGER TR_Spaces_Owner_UPDATE BEFORE UPDATE ON Spaces
                WHEN NEW.TenantId <> OLD.TenantId OR NEW.SpaceId <> OLD.SpaceId OR NEW.Kind <> OLD.Kind OR NEW.OwnerInstanceId IS NOT OLD.OwnerInstanceId OR NEW.CreatedAt <> OLD.CreatedAt
                BEGIN SELECT RAISE(ABORT, 'Space ownership is immutable'); END;
                CREATE TRIGGER TR_SpaceMembership_Private_INSERT BEFORE INSERT ON SpaceMemberships
                WHEN EXISTS(SELECT 1 FROM Spaces s WHERE s.TenantId=NEW.TenantId AND s.SpaceId=NEW.SpaceId AND s.Kind=0 AND s.OwnerInstanceId<>NEW.InstanceId)
                BEGIN SELECT RAISE(ABORT, 'Private membership must match its owner'); END;
                CREATE TRIGGER TR_SpaceMembership_Owner_UPDATE BEFORE UPDATE ON SpaceMemberships
                WHEN NEW.TenantId <> OLD.TenantId OR NEW.SpaceId <> OLD.SpaceId OR NEW.InstanceId <> OLD.InstanceId OR NEW.CreatedAt <> OLD.CreatedAt
                BEGIN SELECT RAISE(ABORT, 'Membership ownership is immutable'); END;
                CREATE TRIGGER TR_SpaceMembership_Private_DELETE BEFORE DELETE ON SpaceMemberships
                WHEN EXISTS(SELECT 1 FROM Spaces s JOIN SystemIdentities i ON i.TenantId=s.TenantId AND i.AgentInstanceId=s.OwnerInstanceId WHERE s.TenantId=OLD.TenantId AND s.SpaceId=OLD.SpaceId AND s.Kind=0 AND i.Status<>2)
                BEGIN SELECT RAISE(ABORT, 'Private membership is system managed'); END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE SpaceDowngradeGuard (Rows INTEGER CHECK(Rows=0));
                INSERT INTO SpaceDowngradeGuard SELECT count(*) FROM Spaces;
                DROP TABLE SpaceDowngradeGuard;
                DROP TRIGGER TR_Spaces_Owner_UPDATE;
                DROP TRIGGER TR_SpaceMembership_Private_INSERT;
                DROP TRIGGER TR_SpaceMembership_Owner_UPDATE;
                DROP TRIGGER TR_SpaceMembership_Private_DELETE;
                """);
            migrationBuilder.DropTable(
                name: "SpaceMembershipAddedBindings");

            migrationBuilder.DropTable(
                name: "SpaceMembershipDisabledBindings");

            migrationBuilder.DropTable(
                name: "SpaceMemberships");

            migrationBuilder.DropTable(
                name: "Spaces");
        }
    }
}
