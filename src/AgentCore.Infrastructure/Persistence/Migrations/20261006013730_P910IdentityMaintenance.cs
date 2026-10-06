using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P910IdentityMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DerivedFromMemoryIdsJson",
                table: "StructuredMemories",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceOrigin",
                table: "StructuredMemories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "IdentityMaintenanceSettings",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    AllowAgentConsolidation = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMaintenanceSettings", x => x.AgentInstanceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IdentityMaintenanceSettings");

            migrationBuilder.DropColumn(
                name: "DerivedFromMemoryIdsJson",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "MaintenanceOrigin",
                table: "StructuredMemories");
        }
    }
}
