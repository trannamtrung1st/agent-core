using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundOccurrenceIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_TriggerOccurrences_BackgroundLink",
                table: "TriggerOccurrences",
                sql: "(BackgroundSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (BackgroundSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId",
                table: "TriggerOccurrences",
                column: "AcceptedAgentRunId",
                principalTable: "AgentRuns",
                principalColumn: "AgentRunId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TriggerOccurrences_Sessions_BackgroundSessionId",
                table: "TriggerOccurrences",
                column: "BackgroundSessionId",
                principalTable: "Sessions",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId",
                table: "TriggerOccurrences");

            migrationBuilder.DropForeignKey(
                name: "FK_TriggerOccurrences_Sessions_BackgroundSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TriggerOccurrences_BackgroundLink",
                table: "TriggerOccurrences");
        }
    }
}
