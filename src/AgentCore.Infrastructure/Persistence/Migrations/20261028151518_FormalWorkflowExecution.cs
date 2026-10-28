using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormalWorkflowExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);

            migrationBuilder.CreateTable(
                name: "TenantWorkflowExecutions",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceScope = table.Column<string>(type: "TEXT", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflowExecutions", x => new { x.TenantId, x.ExecutionId });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowExecutions_TenantWorkflowVersions_TenantId_WorkflowId_Version",
                        columns: x => new { x.TenantId, x.WorkflowId, x.Version },
                        principalTable: "TenantWorkflowVersions",
                        principalColumns: new[] { "TenantId", "WorkflowId", "Version" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantWorkflowSteps",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflowSteps", x => new { x.TenantId, x.ExecutionId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowSteps_TenantWorkflowExecutions_TenantId_ExecutionId",
                        columns: x => new { x.TenantId, x.ExecutionId },
                        principalTable: "TenantWorkflowExecutions",
                        principalColumns: new[] { "TenantId", "ExecutionId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowExecutions_Status_CreatedAt_ExecutionId",
                table: "TenantWorkflowExecutions",
                columns: new[] { "Status", "CreatedAt", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowExecutions_TenantId_SourceScope_RequestKey",
                table: "TenantWorkflowExecutions",
                columns: new[] { "TenantId", "SourceScope", "RequestKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowExecutions_TenantId_WorkflowId_ExecutionId",
                table: "TenantWorkflowExecutions",
                columns: new[] { "TenantId", "WorkflowId", "ExecutionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowExecutions_TenantId_WorkflowId_Version",
                table: "TenantWorkflowExecutions",
                columns: new[] { "TenantId", "WorkflowId", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowSteps_TenantId_OperationId",
                table: "TenantWorkflowSteps",
                columns: new[] { "TenantId", "OperationId" },
                unique: true);

            migrationBuilder.Sql(Read("Up"));
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE WorkflowExecutionRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_workflow_backup_required CHECK(Rows=0));
                INSERT INTO WorkflowExecutionRollbackGuard SELECT count(*) FROM TenantWorkflowExecutions;
                INSERT INTO WorkflowExecutionRollbackGuard SELECT count(*) FROM TenantDelegatedTasks WHERE WorkflowExecutionId IS NOT NULL;
                DROP TABLE WorkflowExecutionRollbackGuard;
                """);
            migrationBuilder.Sql("PRAGMA foreign_keys=OFF;", suppressTransaction: true);
            migrationBuilder.Sql(Read("Down"));
            migrationBuilder.DropTable("TenantWorkflowSteps");
            migrationBuilder.DropTable("TenantWorkflowExecutions");
            migrationBuilder.Sql("PRAGMA foreign_keys=ON;", suppressTransaction: true);
        }
        private static string Read(string direction)
        {
            using var stream = typeof(FormalWorkflowExecution).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261028151518_FormalWorkflowExecution." + direction + ".sql")
                ?? throw new InvalidOperationException("Frozen workflow migration SQL is missing.");
            using var reader = new System.IO.StreamReader(stream); return reader.ReadToEnd();
        }
    }
}
