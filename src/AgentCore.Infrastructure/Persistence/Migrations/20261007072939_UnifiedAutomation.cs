using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // These pre-release Runs and trigger records are disposable; no legacy provenance survives.
            migrationBuilder.Sql("DELETE FROM WorkAttentionAlerts; DELETE FROM WorkApprovals; DELETE FROM WorkCaptures; DELETE FROM WorkItems; DELETE FROM TriggerOccurrences; DELETE FROM ExternalEventDeliveries; DELETE FROM ExternalEvents; DELETE FROM Experiences;");
            migrationBuilder.DropTable(
                name: "ContinuityMaintenanceSettings");

            migrationBuilder.DropTable(
                name: "TriggerRegistrations");

            migrationBuilder.RenameColumn(
                name: "RegistrationId",
                table: "WorkItems",
                newName: "AutomationId");

            migrationBuilder.RenameColumn(
                name: "RegistrationId",
                table: "TriggerOccurrences",
                newName: "AutomationId");

            migrationBuilder.RenameColumn(
                name: "RegistrationId",
                table: "ExternalEventDeliveries",
                newName: "AutomationId");

            migrationBuilder.RenameIndex(
                name: "IX_ExternalEventDeliveries_Status_EventId_RegistrationId",
                table: "ExternalEventDeliveries",
                newName: "IX_ExternalEventDeliveries_Status_EventId_AutomationId");

            migrationBuilder.CreateTable(
                name: "Automations",
                columns: table => new
                {
                    AutomationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    TriggerKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    NextOccurrenceAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorizationOrigin = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SuspensionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ModelOverrideCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelOverrideReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequiresVision = table.Column<bool>(type: "INTEGER", nullable: false),
                    EventSourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Automations", x => x.AutomationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Automations_AgentInstanceId_ProfileId_Status",
                table: "Automations",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Automations_EventSourceId_EventType_Status",
                table: "Automations",
                columns: new[] { "EventSourceId", "EventType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Automations_Status_NextOccurrenceAtUtc_AutomationId",
                table: "Automations",
                columns: new[] { "Status", "NextOccurrenceAtUtc", "AutomationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Automations");

            migrationBuilder.RenameColumn(
                name: "AutomationId",
                table: "WorkItems",
                newName: "RegistrationId");

            migrationBuilder.RenameColumn(
                name: "AutomationId",
                table: "TriggerOccurrences",
                newName: "RegistrationId");

            migrationBuilder.RenameColumn(
                name: "AutomationId",
                table: "ExternalEventDeliveries",
                newName: "RegistrationId");

            migrationBuilder.RenameIndex(
                name: "IX_ExternalEventDeliveries_Status_EventId_AutomationId",
                table: "ExternalEventDeliveries",
                newName: "IX_ExternalEventDeliveries_Status_EventId_RegistrationId");

            migrationBuilder.CreateTable(
                name: "ContinuityMaintenanceSettings",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    LastMaintenanceAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContinuityMaintenanceSettings", x => x.AgentInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "TriggerRegistrations",
                columns: table => new
                {
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AuthorizationOrigin = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EventSourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Intent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ModelOverrideCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelOverrideReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NextOccurrenceAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RequiresVision = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    ScheduleKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SuspensionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerRegistrations", x => x.RegistrationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_AgentInstanceId_ProfileId_Status",
                table: "TriggerRegistrations",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_EventSourceId_EventType_Status",
                table: "TriggerRegistrations",
                columns: new[] { "EventSourceId", "EventType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_Status_NextOccurrenceAtUtc_RegistrationId",
                table: "TriggerRegistrations",
                columns: new[] { "Status", "NextOccurrenceAtUtc", "RegistrationId" });
        }
    }
}
