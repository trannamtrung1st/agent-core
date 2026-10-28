using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormalWorkflowPublicationIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantWorkflowPublicationIntents",
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
                    table.PrimaryKey("PK_TenantWorkflowPublicationIntents", x => new { x.TenantId, x.ActorId, x.RequestKey });
                    table.ForeignKey(
                        name: "FK_TenantWorkflowPublicationIntents_TenantWorkflows_TenantId_WorkflowId",
                        columns: x => new { x.TenantId, x.WorkflowId },
                        principalTable: "TenantWorkflows",
                        principalColumns: new[] { "TenantId", "WorkflowId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantWorkflowPublicationIntents_TenantId_WorkflowId",
                table: "TenantWorkflowPublicationIntents",
                columns: new[] { "TenantId", "WorkflowId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE WorkflowPublicationRollbackGuard(Rows INTEGER CONSTRAINT restore_coordinated_workflow_backup_required CHECK(Rows=0));
                INSERT INTO WorkflowPublicationRollbackGuard SELECT count(*) FROM TenantWorkflowPublicationIntents;
                DROP TABLE WorkflowPublicationRollbackGuard;
                """);
            migrationBuilder.DropTable(
                name: "TenantWorkflowPublicationIntents");
        }
    }
}
