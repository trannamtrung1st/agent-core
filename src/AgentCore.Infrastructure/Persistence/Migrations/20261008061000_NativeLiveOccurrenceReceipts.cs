using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class NativeLiveOccurrenceReceipts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("LiveSessionId", "TriggerOccurrences", type: "TEXT", maxLength: 36, nullable: true);
        migrationBuilder.AddColumn<long>("LiveEvaluationCompletedAtUtc", "TriggerOccurrences", type: "INTEGER", nullable: true);
        migrationBuilder.DropCheckConstraint("CK_TriggerOccurrences_BackgroundLink", "TriggerOccurrences");
        migrationBuilder.AddCheckConstraint("CK_TriggerOccurrences_BackgroundLink", "TriggerOccurrences",
            "(BackgroundSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (BackgroundSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (BackgroundSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)");
        migrationBuilder.CreateIndex("IX_TriggerOccurrences_LiveSessionId", "TriggerOccurrences", "LiveSessionId");
        migrationBuilder.AddForeignKey("FK_TriggerOccurrences_Sessions_LiveSessionId", "TriggerOccurrences", "LiveSessionId",
            "Sessions", principalColumn: "SessionId", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey("FK_TriggerOccurrences_Sessions_LiveSessionId", "TriggerOccurrences");
        migrationBuilder.DropIndex("IX_TriggerOccurrences_LiveSessionId", "TriggerOccurrences");
        migrationBuilder.DropCheckConstraint("CK_TriggerOccurrences_BackgroundLink", "TriggerOccurrences");
        migrationBuilder.AddCheckConstraint("CK_TriggerOccurrences_BackgroundLink", "TriggerOccurrences",
            "(BackgroundSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (BackgroundSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL)");
        migrationBuilder.DropColumn("LiveSessionId", "TriggerOccurrences");
        migrationBuilder.DropColumn("LiveEvaluationCompletedAtUtc", "TriggerOccurrences");
    }
}
