using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailMonitoringEventSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationTriggers_Source",
                table: "AutomationTriggers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationTriggers_Source",
                table: "AutomationTriggers",
                sql: "(Kind = 0 AND ScheduleJson IS NOT NULL AND EventId IS NULL AND CoreEventKey IS NULL AND FilterExpression IS NULL AND DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (Kind = 1 AND ScheduleJson IS NULL AND EventId IS NOT NULL AND CoreEventKey IS NULL) OR (Kind = 2 AND ScheduleJson IS NULL AND EventId IS NULL AND CoreEventKey IN ('run.completed','run.failed','session.completed','session.ended','instance.config_changed','harness.definition_adopted','email.message.received'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AutomationTriggers_Source",
                table: "AutomationTriggers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AutomationTriggers_Source",
                table: "AutomationTriggers",
                sql: "(Kind = 0 AND ScheduleJson IS NOT NULL AND EventId IS NULL AND CoreEventKey IS NULL AND FilterExpression IS NULL AND DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (Kind = 1 AND ScheduleJson IS NULL AND EventId IS NOT NULL AND CoreEventKey IS NULL) OR (Kind = 2 AND ScheduleJson IS NULL AND EventId IS NULL AND CoreEventKey IN ('run.completed','run.failed','session.completed','session.ended','instance.config_changed','harness.definition_adopted'))");
        }
    }
}
