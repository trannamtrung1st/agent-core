using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P910MaintenanceProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Preview", table: "WorkApprovals", type: "TEXT", maxLength: 12000, nullable: false,
                oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 2000);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceAgentInstanceId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceSessionId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaintenanceWorkItemId",
                table: "StructuredMemories",
                type: "TEXT",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Preview", table: "WorkApprovals", type: "TEXT", maxLength: 2000, nullable: false,
                oldClrType: typeof(string), oldType: "TEXT", oldMaxLength: 12000);

            migrationBuilder.DropColumn(
                name: "MaintenanceAgentInstanceId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "MaintenanceSessionId",
                table: "StructuredMemories");

            migrationBuilder.DropColumn(
                name: "MaintenanceWorkItemId",
                table: "StructuredMemories");
        }
    }
}
