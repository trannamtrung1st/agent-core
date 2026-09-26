using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7ConversationTurnExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationTurnExecutions",
                columns: table => new
                {
                    ExecutionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceUserEntryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResponseId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PinnedPersonaJson = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ClaimGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    AssistantEntryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    AcceptedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationTurnExecutions", x => x.ExecutionId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnExecutions_SessionId_SourceEventId",
                table: "ConversationTurnExecutions",
                columns: new[] { "SessionId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnExecutions_SessionId_Status",
                table: "ConversationTurnExecutions",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnExecutions_Status_ClaimLeaseExpiresAtUtc",
                table: "ConversationTurnExecutions",
                columns: new[] { "Status", "ClaimLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationTurnExecutions_ResponseId",
                table: "ConversationTurnExecutions",
                column: "ResponseId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationTurnExecutions");
        }
    }
}
