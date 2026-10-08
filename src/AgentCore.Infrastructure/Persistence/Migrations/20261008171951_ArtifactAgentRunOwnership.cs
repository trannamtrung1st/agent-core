using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactAgentRunOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentRunId",
                table: "Artifacts",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_SessionId_AgentRunId",
                table: "Artifacts",
                columns: new[] { "SessionId", "AgentRunId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Artifacts_SessionId_AgentRunId",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "AgentRunId",
                table: "Artifacts");
        }
    }
}
