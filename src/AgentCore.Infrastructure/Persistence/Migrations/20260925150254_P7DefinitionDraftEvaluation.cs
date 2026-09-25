using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7DefinitionDraftEvaluation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftEvaluationResults",
                columns: table => new
                {
                    ResultId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    FindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftEvaluationResults", x => x.ResultId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftEvaluationScenarios",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ScenarioId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RequirementLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckType = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftEvaluationScenarios", x => new { x.DraftId, x.ScenarioId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftEvaluationResults_DraftId_ScenarioId_RecordedAtUtc",
                table: "AgentDefinitionDraftEvaluationResults",
                columns: new[] { "DraftId", "ScenarioId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftEvaluationScenarios_DraftId",
                table: "AgentDefinitionDraftEvaluationScenarios",
                column: "DraftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftEvaluationResults");

            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftEvaluationScenarios");
        }
    }
}
