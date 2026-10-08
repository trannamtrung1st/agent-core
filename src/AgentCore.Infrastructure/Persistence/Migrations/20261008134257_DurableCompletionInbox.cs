using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableCompletionInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns");

            migrationBuilder.AddColumn<string>(
                name: "InboxJson",
                table: "BackgroundCompletionReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "BackgroundCompletionReceipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                UPDATE BackgroundCompletionReceipts SET InboxJson = (
                  SELECT json_object('childAgentRunId', receipt.ChildAgentRunId,
                    'owner', json_object('agentInstanceId', receipt.AgentInstanceId, 'profileId', receipt.ProfileId),
                    'parentSessionId', json_extract(session.OriginJson, '$.originatingSessionId'),
                    'childSessionId', run.SessionId,
                    'sourceFinishedAtUtc', strftime('%Y-%m-%dT%H:%M:%fZ', run.UpdatedAtUtc / 1000.0, 'unixepoch'), 'revision', 1,
                    'status', CASE WHEN receipt.SkipReason IS NOT NULL THEN 5
                      WHEN EXISTS (SELECT 1 FROM AgentRuns report WHERE report.ActivationId = receipt.ParentActivationId
                        AND report.Status = 4 AND json_extract(report.PayloadJson, '$.result.outcomeEntryId') IS NOT NULL) THEN 4 ELSE 3 END,
                    'reportActivationId', receipt.ParentActivationId, 'skipReason', receipt.SkipReason)
                  FROM AgentRuns run JOIN Sessions session ON session.SessionId = run.SessionId
                  WHERE run.AgentRunId = receipt.ChildAgentRunId)
                FROM BackgroundCompletionReceipts AS receipt WHERE BackgroundCompletionReceipts.ChildAgentRunId = receipt.ChildAgentRunId;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 2, 7)");

            migrationBuilder.AddColumn<long>(
                name: "ClaimExpiresAtUtc",
                table: "BackgroundCompletionReceipts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimRunId",
                table: "BackgroundCompletionReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentSessionId",
                table: "BackgroundCompletionReceipts",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "BackgroundCompletionReceipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE BackgroundCompletionReceipts SET ParentSessionId = json_extract(InboxJson, '$.parentSessionId'),
                  Status = json_extract(InboxJson, '$.status'), Revision = json_extract(InboxJson, '$.revision'),
                  ClaimRunId = json_extract(InboxJson, '$.claimRunId'),
                  ClaimExpiresAtUtc = CAST((julianday(json_extract(InboxJson, '$.claimExpiresAtUtc')) - 2440587.5) * 86400000 AS INTEGER);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_AgentInstanceId_ProfileId_ParentSessionId_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts",
                columns: new[] { "AgentInstanceId", "ProfileId", "ParentSessionId", "CreatedAtUtc", "ChildAgentRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ClaimRunId",
                table: "BackgroundCompletionReceipts",
                column: "ClaimRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_Status_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts",
                columns: new[] { "Status", "CreatedAtUtc", "ChildAgentRunId" });

            migrationBuilder.DropIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts",
                column: "ParentActivationId",
                filter: "ParentActivationId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts",
                column: "ParentActivationId",
                unique: true,
                filter: "ParentActivationId IS NOT NULL");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundCompletionReceipts_AgentInstanceId_ProfileId_ParentSessionId_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundCompletionReceipts_ClaimRunId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropIndex(
                name: "IX_BackgroundCompletionReceipts_Status_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropColumn(
                name: "ClaimExpiresAtUtc",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropColumn(
                name: "ClaimRunId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropColumn(
                name: "ParentSessionId",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "InboxJson",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "BackgroundCompletionReceipts");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 2)");
        }
    }
}
