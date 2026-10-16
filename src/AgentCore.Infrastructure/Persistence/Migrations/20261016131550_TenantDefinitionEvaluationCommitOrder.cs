using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDefinitionEvaluationCommitOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CommitRevision",
                table: "AgentDefinitionDraftEvaluationResults",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftEvaluationResults_DraftId_ScenarioId_CommitRevision",
                table: "AgentDefinitionDraftEvaluationResults",
                columns: new[] { "DraftId", "ScenarioId", "CommitRevision" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentDefinitionDraftEvaluationResults_DraftId_ScenarioId_CommitRevision",
                table: "AgentDefinitionDraftEvaluationResults");

            migrationBuilder.DropColumn(
                name: "CommitRevision",
                table: "AgentDefinitionDraftEvaluationResults");
        }
    }
}
