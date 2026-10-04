using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P96ExternalEventDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExternalEventDeliveries",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEventDeliveries", x => new { x.EventId, x.RegistrationId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEventDeliveries_Status_EventId_RegistrationId",
                table: "ExternalEventDeliveries",
                columns: new[] { "Status", "EventId", "RegistrationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExternalEventDeliveries");
        }
    }
}
