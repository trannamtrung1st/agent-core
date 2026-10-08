using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireLegacyExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationTurnExecutions");

            migrationBuilder.DropTable(
                name: "WorkApprovals");

            migrationBuilder.DropTable(
                name: "WorkAttentionAlerts");

            migrationBuilder.DropTable(
                name: "WorkCaptures");

            migrationBuilder.DropTable(
                name: "WorkItems");

            migrationBuilder.DropColumn(
                name: "DurableWorkItemId",
                table: "TriggerOccurrences");

            migrationBuilder.RenameColumn(
                name: "MaintenanceWorkItemId",
                table: "StructuredMemories",
                newName: "MaintenanceAgentRunId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "MaintenanceAgentRunId",
                table: "StructuredMemories",
                newName: "MaintenanceWorkItemId");

            migrationBuilder.AddColumn<string>(
                name: "DurableWorkItemId",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ConversationTurnExecutions",
                columns: table => new
                {
                    ExecutionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AcceptedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ActiveSkillKeysJson = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    AssistantEntryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CapabilityLoadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    LoadedCapabilityIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PinnedPersonaJson = table.Column<string>(type: "TEXT", nullable: true),
                    PinnedSkillCatalogJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ResponseId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SkillLoadCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceUserEntryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationTurnExecutions", x => x.ExecutionId);
                });

            migrationBuilder.CreateTable(
                name: "WorkAttentionAlerts",
                columns: table => new
                {
                    AlertKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkAttentionAlerts", x => x.AlertKey);
                });

            migrationBuilder.CreateTable(
                name: "WorkCaptures",
                columns: table => new
                {
                    CaptureId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    RetainUntilUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCaptures", x => x.CaptureId);
                });

            migrationBuilder.CreateTable(
                name: "WorkItems",
                columns: table => new
                {
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CancellationRequested = table.Column<bool>(type: "INTEGER", nullable: false),
                    CancellationRequestedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CheckpointJson = table.Column<string>(type: "TEXT", maxLength: 65536, nullable: true),
                    CheckpointOutputBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    CheckpointRemainingOverallBudgetMs = table.Column<int>(type: "INTEGER", nullable: true),
                    CheckpointStepCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ClaimGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CurrentApprovalId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    FailureAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FailureDiagnosticId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    FailureSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    KnownEffectSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NextRetryAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonaName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    PinnedPersonaJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProgressSummary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ProgressUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultAttentionRequired = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    ResultCompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultText = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduledAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SideEffectActionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SideEffectDisposition = table.Column<int>(type: "INTEGER", nullable: false),
                    SideEffectToolCallId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    SideEffectUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceOccurrenceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
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
                    ActionHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CheckpointRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Consumed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Decision = table.Column<int>(type: "INTEGER", nullable: false),
                    ExecutionGeneration = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PreparedActionJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 12000, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false)
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
                name: "IX_ConversationTurnExecutions_ResponseId",
                table: "ConversationTurnExecutions",
                column: "ResponseId");

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
                name: "IX_WorkApprovals_WorkItemId",
                table: "WorkApprovals",
                column: "WorkItemId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkAttentionAlerts_WorkItemId",
                table: "WorkAttentionAlerts",
                column: "WorkItemId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkCaptures_RetainUntilUtc",
                table: "WorkCaptures",
                column: "RetainUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkCaptures_WorkItemId",
                table: "WorkCaptures",
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
    }
}
