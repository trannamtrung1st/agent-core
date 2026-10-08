using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomationDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE AgentRuns SET PayloadJson = json_set(PayloadJson, '$.admission.outputContract', CASE WHEN json_extract(PayloadJson, '$.admission.activation.kind') = 6 THEN 2 WHEN EXISTS (SELECT 1 FROM Sessions WHERE Sessions.SessionId = AgentRuns.SessionId AND json_extract(Sessions.OriginJson, '$.initialBackgroundAgentRunId') = AgentRuns.AgentRunId) THEN 1 ELSE 0 END)");
            migrationBuilder.DropForeignKey(
                name: "FK_TriggerOccurrences_Sessions_BackgroundSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_BackgroundSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TriggerOccurrences_BackgroundLink",
                table: "TriggerOccurrences");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionId",
                table: "AgentRuns");

            migrationBuilder.RenameColumn(
                name: "BackgroundSessionId",
                table: "TriggerOccurrences",
                newName: "ExecutionSessionId");

            migrationBuilder.AddColumn<int>(
                name: "ExecutionTargetKind",
                table: "TriggerOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReportToSessionId",
                table: "TriggerOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetSessionId",
                table: "TriggerOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExecutionTargetKind",
                table: "Automations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ReportToSessionId",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresTools",
                table: "Automations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TargetSessionId",
                table: "Automations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_ExecutionSessionId",
                table: "TriggerOccurrences",
                column: "ExecutionSessionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TriggerOccurrences_Destination",
                table: "TriggerOccurrences",
                sql: "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TriggerOccurrences_ExecutionLink",
                table: "TriggerOccurrences",
                sql: "(ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Automations_Destination",
                table: "Automations",
                sql: "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 2)");

            migrationBuilder.AddForeignKey(
                name: "FK_TriggerOccurrences_Sessions_ExecutionSessionId",
                table: "TriggerOccurrences",
                column: "ExecutionSessionId",
                principalTable: "Sessions",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TriggerOccurrences_Sessions_ExecutionSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_ExecutionSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TriggerOccurrences_Destination",
                table: "TriggerOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TriggerOccurrences_ExecutionLink",
                table: "TriggerOccurrences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Automations_Destination",
                table: "Automations");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "ExecutionTargetKind",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ReportToSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "TargetSessionId",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ExecutionTargetKind",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "ReportToSessionId",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "RequiresTools",
                table: "Automations");

            migrationBuilder.DropColumn(
                name: "TargetSessionId",
                table: "Automations");

            migrationBuilder.RenameColumn(
                name: "ExecutionSessionId",
                table: "TriggerOccurrences",
                newName: "BackgroundSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_BackgroundSessionId",
                table: "TriggerOccurrences",
                column: "BackgroundSessionId",
                unique: true,
                filter: "BackgroundSessionId IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TriggerOccurrences_BackgroundLink",
                table: "TriggerOccurrences",
                sql: "(BackgroundSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (BackgroundSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (BackgroundSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionId",
                table: "AgentRuns",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_TriggerOccurrences_Sessions_BackgroundSessionId",
                table: "TriggerOccurrences",
                column: "BackgroundSessionId",
                principalTable: "Sessions",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
