using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P96ExternalEventSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApplicationConnections_WebhookKey",
                table: "ApplicationConnections");

            migrationBuilder.DropColumn(
                name: "WebhookKey",
                table: "ApplicationConnections");

            migrationBuilder.DropColumn(
                name: "WebhookStatus",
                table: "ApplicationConnections");

            migrationBuilder.DropColumn(
                name: "WebhookTokenHash",
                table: "ApplicationConnections");

            migrationBuilder.AddColumn<string>(
                name: "EventSourceId",
                table: "TriggerRegistrations",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EventType",
                table: "TriggerRegistrations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ExternalEventSources",
                columns: table => new
                {
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceKey = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEventSources", x => x.SourceId);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEvents", x => x.EventId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerRegistrations_EventSourceId_EventType_Status",
                table: "TriggerRegistrations",
                columns: new[] { "EventSourceId", "EventType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEventSources_SourceKey",
                table: "ExternalEventSources",
                column: "SourceKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEvents_SourceId_SourceEventId",
                table: "ExternalEvents",
                columns: new[] { "SourceId", "SourceEventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalEventSources");

            migrationBuilder.DropTable(
                name: "ExternalEvents");

            migrationBuilder.DropIndex(
                name: "IX_TriggerRegistrations_EventSourceId_EventType_Status",
                table: "TriggerRegistrations");

            migrationBuilder.DropColumn(
                name: "EventSourceId",
                table: "TriggerRegistrations");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "TriggerRegistrations");

            migrationBuilder.AddColumn<string>(
                name: "WebhookKey",
                table: "ApplicationConnections",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WebhookStatus",
                table: "ApplicationConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WebhookTokenHash",
                table: "ApplicationConnections",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationConnections_WebhookKey",
                table: "ApplicationConnections",
                column: "WebhookKey",
                unique: true);
        }
    }
}
