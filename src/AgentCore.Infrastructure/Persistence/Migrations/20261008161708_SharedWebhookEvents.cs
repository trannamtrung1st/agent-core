using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedWebhookEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.ResourceId);
                });

            // Preserve resource IDs, secrets, subscriptions and delivery history. Existing clients
            // must use the newly assigned Event key; no compatibility endpoint is retained.
            migrationBuilder.Sql("""
                INSERT INTO WebhookEvents (ResourceId, DisplayName, Kind, EventKey, CredentialHash, Status, Revision, CreatedAtUtc, UpdatedAtUtc)
                SELECT SourceId, DisplayName, Kind, 'event.' || lower(replace(SourceKey, '-', '')), CredentialHash, Status, Revision, CreatedAtUtc, UpdatedAtUtc
                FROM ExternalEventSources;
                """);
            migrationBuilder.Sql("""
                UPDATE TriggerOccurrences
                SET DedupeKey = (SELECT 'event:' || e.SourceId || ':' || e.SourceEventId || ':' || TriggerOccurrences.AutomationId
                    FROM ExternalEvents e WHERE e.EventId = TriggerOccurrences.SourceEventId)
                WHERE DedupeKey LIKE 'order.placed:%'
                  AND EXISTS (SELECT 1 FROM ExternalEvents e WHERE e.EventId = TriggerOccurrences.SourceEventId);
                """);
            migrationBuilder.DropTable(name: "ExternalEventSources");

            migrationBuilder.DropIndex(
                name: "IX_Automations_EventSourceId_EventType_Status",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "ExternalEvents");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Automations");

            migrationBuilder.RenameColumn(
                name: "SourceId",
                table: "ExternalEvents",
                newName: "ResourceId");

            migrationBuilder.RenameIndex(
                name: "IX_ExternalEvents_SourceId_SourceEventId",
                table: "ExternalEvents",
                newName: "IX_ExternalEvents_ResourceId_SourceEventId");

            migrationBuilder.RenameColumn(
                name: "EventSourceId",
                table: "Automations",
                newName: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_Automations_EventId_Status",
                table: "Automations",
                columns: new[] { "EventId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_EventKey",
                table: "WebhookEvents",
                column: "EventKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Shared Events have arbitrary keys and payloads. Restore a pre-migration backup to downgrade safely.");
    }
}
