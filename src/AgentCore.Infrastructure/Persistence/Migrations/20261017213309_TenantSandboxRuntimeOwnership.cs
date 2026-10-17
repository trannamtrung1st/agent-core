using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSandboxRuntimeOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorPrincipalId",
                table: "SandboxSessionLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProfileId",
                table: "SandboxSessionLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProfileRevision",
                table: "SandboxSessionLeases",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SandboxSessionLeases",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AgentInstanceId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorPrincipalId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LeaseSessionId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnedRunId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "SandboxExecutions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_ProfileId_ProfileRevision",
                table: "SandboxSessionLeases",
                columns: new[] { "ProfileId", "ProfileRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_TenantId_AgentInstanceId_ExpiresAt",
                table: "SandboxSessionLeases",
                columns: new[] { "TenantId", "AgentInstanceId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_TenantId_ProfileId",
                table: "SandboxSessionLeases",
                columns: new[] { "TenantId", "ProfileId" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxSessionLeases",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxLease_TenantJson",
                table: "SandboxSessionLeases",
                sql: "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND ProfileId IS NULL AND ProfileRevision IS NULL AND json_extract(Json, '$.tenantId') IS NULL AND json_extract(Json, '$.executorPrincipalId') IS NULL AND json_extract(Json, '$.profile.tenantId') IS NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NOT NULL AND ProfileRevision IS NOT NULL AND ProfileRevision > 0 AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId) AND json_type(Json, '$.executorPrincipalId') IS 'text' AND lower(json_extract(Json, '$.executorPrincipalId')) = lower(ExecutorPrincipalId) AND json_type(Json, '$.profile.tenantId') IS 'text' AND lower(json_extract(Json, '$.profile.tenantId')) = lower(TenantId) AND json_extract(Json, '$.profile.id') IS ProfileId AND json_extract(Json, '$.profile.revision') IS ProfileRevision)");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_LeaseSessionId",
                table: "SandboxExecutions",
                column: "LeaseSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_OwnedRunId_SessionId",
                table: "SandboxExecutions",
                columns: new[] { "OwnedRunId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxExecutions",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_TenantId_SessionId_AgentRunId_ToolCallId",
                table: "SandboxExecutions",
                columns: new[] { "TenantId", "SessionId", "AgentRunId", "ToolCallId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxExecution_TenantJson",
                table: "SandboxExecutions",
                sql: "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND AgentInstanceId IS NULL AND OwnedRunId IS NULL AND LeaseSessionId IS NULL AND Revision = 0 AND json_extract(Json, '$.tenantId') IS NULL AND json_extract(Json, '$.executorPrincipalId') IS NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND AgentInstanceId IS NOT NULL AND OwnedRunId IS NOT NULL AND OwnedRunId = AgentRunId AND LeaseSessionId IS NOT NULL AND LeaseSessionId = SessionId AND Revision > 0 AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId) AND json_type(Json, '$.executorPrincipalId') IS 'text' AND lower(json_extract(Json, '$.executorPrincipalId')) = lower(ExecutorPrincipalId) AND json_type(Json, '$.agentInstanceId') IS 'text' AND lower(json_extract(Json, '$.agentInstanceId')) = lower(AgentInstanceId) AND json_extract(Json, '$.revision') IS Revision)");

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxExecutions_AgentRuns_OwnedRunId_SessionId",
                table: "SandboxExecutions",
                columns: new[] { "OwnedRunId", "SessionId" },
                principalTable: "AgentRuns",
                principalColumns: new[] { "AgentRunId", "SessionId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxExecutions_SandboxSessionLeases_LeaseSessionId",
                table: "SandboxExecutions",
                column: "LeaseSessionId",
                principalTable: "SandboxSessionLeases",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxExecutions_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxExecutions",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                principalTable: "TenantSessions",
                principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxSessionLeases_SandboxProfiles_ProfileId_ProfileRevision",
                table: "SandboxSessionLeases",
                columns: new[] { "ProfileId", "ProfileRevision" },
                principalTable: "SandboxProfiles",
                principalColumns: new[] { "Id", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxSessionLeases_TenantSandboxProfiles_TenantId_ProfileId",
                table: "SandboxSessionLeases",
                columns: new[] { "TenantId", "ProfileId" },
                principalTable: "TenantSandboxProfiles",
                principalColumns: new[] { "TenantId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxSessionLeases_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxSessionLeases",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                principalTable: "TenantSessions",
                principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SandboxExecutions_AgentRuns_OwnedRunId_SessionId",
                table: "SandboxExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_SandboxExecutions_SandboxSessionLeases_LeaseSessionId",
                table: "SandboxExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_SandboxExecutions_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_SandboxSessionLeases_SandboxProfiles_ProfileId_ProfileRevision",
                table: "SandboxSessionLeases");

            migrationBuilder.DropForeignKey(
                name: "FK_SandboxSessionLeases_TenantSandboxProfiles_TenantId_ProfileId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropForeignKey(
                name: "FK_SandboxSessionLeases_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropIndex(
                name: "IX_SandboxSessionLeases_ProfileId_ProfileRevision",
                table: "SandboxSessionLeases");

            migrationBuilder.DropIndex(
                name: "IX_SandboxSessionLeases_TenantId_AgentInstanceId_ExpiresAt",
                table: "SandboxSessionLeases");

            migrationBuilder.DropIndex(
                name: "IX_SandboxSessionLeases_TenantId_ProfileId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropIndex(
                name: "IX_SandboxSessionLeases_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxLease_TenantJson",
                table: "SandboxSessionLeases");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_LeaseSessionId",
                table: "SandboxExecutions");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_OwnedRunId_SessionId",
                table: "SandboxExecutions");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "SandboxExecutions");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_TenantId_SessionId_AgentRunId_ToolCallId",
                table: "SandboxExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxExecution_TenantJson",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "ExecutorPrincipalId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropColumn(
                name: "ProfileId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropColumn(
                name: "ProfileRevision",
                table: "SandboxSessionLeases");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SandboxSessionLeases");

            migrationBuilder.DropColumn(
                name: "AgentInstanceId",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "ExecutorPrincipalId",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "LeaseSessionId",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "OwnedRunId",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SandboxExecutions");
        }
    }
}
