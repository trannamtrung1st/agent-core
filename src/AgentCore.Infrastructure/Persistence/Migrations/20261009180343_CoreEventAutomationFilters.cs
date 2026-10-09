using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoreEventAutomationFilters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DecisionJson",
                table: "ExternalEventDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SnapshotJson",
                table: "ExternalEventDeliveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CoreEventKey",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DispatchMode",
                table: "Automations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "DispatchWindowSeconds",
                table: "Automations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FilterExpression",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PresetId",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PresetVersion",
                table: "Automations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CoreEventBuckets",
                columns: table => new
                {
                    BucketId = table.Column<string>(type: "TEXT", nullable: false),
                    CoverageJson = table.Column<string>(type: "TEXT", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    DueAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Flushed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEventBuckets", x => x.BucketId);
                });

            migrationBuilder.CreateTable(
                name: "CoreEventDeliveries",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEventDeliveries", x => new { x.EventId, x.AutomationId });
                });

            migrationBuilder.CreateTable(
                name: "CoreEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Snapshotted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEvents", x => x.EventId);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Automations_Dispatch",
                table: "Automations",
                sql: "(DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (DispatchMode = 1 AND DispatchWindowSeconds BETWEEN 60 AND 3600)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Automations_Preset",
                table: "Automations",
                sql: "(PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Automations_Trigger",
                table: "Automations",
                sql: "(TriggerKind = 0 AND ScheduleJson IS NOT NULL AND EventId IS NULL AND CoreEventKey IS NULL AND FilterExpression IS NULL AND DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (TriggerKind = 1 AND ScheduleJson IS NULL AND EventId IS NOT NULL AND CoreEventKey IS NULL) OR (TriggerKind = 2 AND ScheduleJson IS NULL AND EventId IS NULL AND CoreEventKey IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CoreEventBuckets_Flushed_DueAtUtc",
                table: "CoreEventBuckets",
                columns: new[] { "Flushed", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreEventDeliveries_AgentInstanceId_ProfileId_Status",
                table: "CoreEventDeliveries",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreEvents_DedupeKey",
                table: "CoreEvents",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoreEvents_Snapshotted_ReceivedAtUtc_EventId",
                table: "CoreEvents",
                columns: new[] { "Snapshotted", "ReceivedAtUtc", "EventId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CoreEventBuckets");

            migrationBuilder.DropTable(
                name: "CoreEventDeliveries");

            migrationBuilder.DropTable(
                name: "CoreEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Automations_Dispatch",
                table: "Automations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Automations_Preset",
                table: "Automations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Automations_Trigger",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "DecisionJson",
                table: "ExternalEventDeliveries");

            migrationBuilder.DropColumn(
                name: "SnapshotJson",
                table: "ExternalEventDeliveries");

            migrationBuilder.DropColumn(
                name: "CoreEventKey",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "DispatchMode",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "DispatchWindowSeconds",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "FilterExpression",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "PresetId",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "PresetVersion",
                table: "Automations");
        }
    }
}
