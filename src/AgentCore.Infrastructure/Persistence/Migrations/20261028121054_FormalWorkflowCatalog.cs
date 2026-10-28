using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormalWorkflowCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantWorkflows",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflows", x => new { x.TenantId, x.WorkflowId });
                    table.ForeignKey(
                        name: "FK_TenantWorkflows_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantWorkflowCatalogReceipts",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestKey = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflowCatalogReceipts", x => new { x.TenantId, x.ActorId, x.RequestKey });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowCatalogReceipts_TenantWorkflows_TenantId_WorkflowId",
                        columns: x => new { x.TenantId, x.WorkflowId },
                        principalTable: "TenantWorkflows",
                        principalColumns: new[] { "TenantId", "WorkflowId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantWorkflowVersions",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWorkflowVersions", x => new { x.TenantId, x.WorkflowId, x.Version });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowVersions_TenantWorkflows_TenantId_WorkflowId",
                        columns: x => new { x.TenantId, x.WorkflowId },
                        principalTable: "TenantWorkflows",
                        principalColumns: new[] { "TenantId", "WorkflowId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowCatalogReceipts_TenantId_WorkflowId",
                table: "TenantWorkflowCatalogReceipts",
                columns: new[] { "TenantId", "WorkflowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE WorkflowCatalogRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_workflow_backup_required CHECK(Rows=0));
                INSERT INTO WorkflowCatalogRollbackGuard SELECT count(*) FROM TenantWorkflows;
                DROP TABLE WorkflowCatalogRollbackGuard;
                """);
            migrationBuilder.DropTable(
                name: "TenantWorkflowCatalogReceipts");

            migrationBuilder.DropTable(
                name: "TenantWorkflowVersions");

            migrationBuilder.DropTable(
                name: "TenantWorkflows");
        }
    }
}
