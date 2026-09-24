using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkItemContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    NextRetryAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    KnownEffectSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ProgressSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ProgressUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CheckpointJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    CheckpointStepCount = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckpointOutputBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CheckpointRemainingOverallBudgetMs = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultText = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    ResultCompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailureSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    FailureAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SideEffectDisposition = table.Column<int>(type: "INTEGER", nullable: false),
                    SideEffectActionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SideEffectUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CurrentApprovalId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceOccurrenceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    RegistrationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ScheduledAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonaName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItems", x => x.WorkItemId);
                });

            migrationBuilder.CreateTable(
                name: "WorkApprovals",
                columns: table => new
                {
                    ApprovalId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ExecutionGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    CheckpointRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    PreparedActionJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkApprovals", x => x.ApprovalId);
                    table.ForeignKey(
                        name: "FK_WorkApprovals_WorkItems_WorkItemId",
                        column: x => x.WorkItemId,
                        principalTable: "WorkItems",
                        principalColumn: "WorkItemId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkApprovals_WorkItemId",
                table: "WorkApprovals",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_AgentInstanceId_ProfileId_CreatedAtUtc",
                table: "WorkItems",
                columns: new[] { "AgentInstanceId", "ProfileId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_SourceOccurrenceId",
                table: "WorkItems",
                column: "SourceOccurrenceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Status_ClaimLeaseExpiresAtUtc",
                table: "WorkItems",
                columns: new[] { "Status", "ClaimLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItems_Status_NextRetryAtUtc",
                table: "WorkItems",
                columns: new[] { "Status", "NextRetryAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkApprovals");

            migrationBuilder.DropTable(
                name: "WorkItems");
        }
    }
}
