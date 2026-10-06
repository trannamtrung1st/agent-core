using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ConfigurableContinuityCadence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContinuityMaintenanceSettings",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    LastMaintenanceAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContinuityMaintenanceSettings", x => x.AgentInstanceId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContinuityMaintenanceSettings");
        }
    }
}
