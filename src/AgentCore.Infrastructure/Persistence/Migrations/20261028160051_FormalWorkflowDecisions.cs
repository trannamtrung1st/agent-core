using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormalWorkflowDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantWorkflowDecisions",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflowDecisions", x => new { x.TenantId, x.ExecutionId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowDecisions_TenantWorkflowSteps_TenantId_ExecutionId_NodeId",
                        columns: x => new { x.TenantId, x.ExecutionId, x.NodeId },
                        principalTable: "TenantWorkflowSteps",
                        principalColumns: new[] { "TenantId", "ExecutionId", "NodeId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowDecisions_TenantId_ActorId_RequestKey",
                table: "TenantWorkflowDecisions",
                columns: new[] { "TenantId", "ActorId", "RequestKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE WorkflowDecisionRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_workflow_backup_required CHECK(Rows=0));
                INSERT INTO WorkflowDecisionRollbackGuard SELECT count(*) FROM TenantWorkflowDecisions;
                DROP TABLE WorkflowDecisionRollbackGuard;
                """);
            migrationBuilder.DropTable(
                name: "TenantWorkflowDecisions");
        }
    }
}
