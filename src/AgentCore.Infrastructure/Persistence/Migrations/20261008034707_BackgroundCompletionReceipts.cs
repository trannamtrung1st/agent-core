using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundCompletionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackgroundCompletionReceipts",
                columns: table => new
                {
                    ChildAgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentActivationId = table.Column<string>(type: "TEXT", nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundCompletionReceipts", x => x.ChildAgentRunId);
                    table.ForeignKey(
                        name: "FK_BackgroundCompletionReceipts_Activations_ParentActivationId",
                        column: x => x.ParentActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackgroundCompletionReceipts_AgentRuns_ChildAgentRunId",
                        column: x => x.ChildAgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts",
                column: "ParentActivationId",
                unique: true,
                filter: "ParentActivationId IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackgroundCompletionReceipts");
        }
    }
}
