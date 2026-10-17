using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReleaseTenantApprovalExecutionSlot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 7) OR (Status = 2 AND TenantId IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 2, 7)");
        }
    }
}
