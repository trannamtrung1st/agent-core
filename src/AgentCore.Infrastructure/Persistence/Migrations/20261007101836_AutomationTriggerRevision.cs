using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationTriggerRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "ScheduleRevision",
                table: "TriggerOccurrences",
                newName: "TriggerRevision");

            migrationBuilder.RenameColumn(
                name: "ScheduleRevision",
                table: "Automations",
                newName: "TriggerRevision");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TriggerRevision",
                table: "TriggerOccurrences",
                newName: "ScheduleRevision");

            migrationBuilder.RenameColumn(
                name: "TriggerRevision",
                table: "Automations",
                newName: "ScheduleRevision");
        }
    }
}
