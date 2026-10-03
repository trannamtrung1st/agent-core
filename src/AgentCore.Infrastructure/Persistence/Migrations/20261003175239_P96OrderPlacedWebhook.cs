using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P96OrderPlacedWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
        }
    }
}
