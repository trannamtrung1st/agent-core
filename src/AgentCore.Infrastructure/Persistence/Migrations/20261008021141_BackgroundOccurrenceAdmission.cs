using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundOccurrenceAdmission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcceptedAgentRunId",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundSessionId",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AcceptedAgentRunId",
                table: "TriggerOccurrences",
                column: "AcceptedAgentRunId",
                unique: true,
                filter: "AcceptedAgentRunId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_BackgroundSessionId",
                table: "TriggerOccurrences",
                column: "BackgroundSessionId",
                unique: true,
                filter: "BackgroundSessionId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_AcceptedAgentRunId",
                table: "TriggerOccurrences");

            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_BackgroundSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "AcceptedAgentRunId",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "BackgroundSessionId",
                table: "TriggerOccurrences");
        }
    }
}
