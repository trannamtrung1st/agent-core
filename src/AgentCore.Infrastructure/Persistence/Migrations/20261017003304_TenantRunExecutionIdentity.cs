using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantRunExecutionIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF rebuilds dependent execution tables before TenantSessions. Make its new referenced key
            // available on the old principal table; the temporary index disappears with that table.
            migrationBuilder.Sql("CREATE UNIQUE INDEX TenantRunSessionMigrationKey ON TenantSessions (TenantId, SessionId, AgentInstanceId, ExecutorPrincipalId);");

            migrationBuilder.AlterColumn<string>(
                name: "ProfileId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorPrincipalId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalInitiatorPrincipalId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProfileId",
                table: "Activations",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "ExecutorPrincipalId",
                table: "Activations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OriginalInitiatorPrincipalId",
                table: "Activations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Activations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "TenantSessions",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_TenantId_OriginalInitiatorPrincipalId",
                table: "AgentRuns",
                columns: new[] { "TenantId", "OriginalInitiatorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "AgentRuns",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_TenantId_SessionId_CreatedAtUtc_AgentRunId",
                table: "AgentRuns",
                columns: new[] { "TenantId", "SessionId", "CreatedAtUtc", "AgentRunId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_AgentRuns_ExecutionOwner",
                table: "AgentRuns",
                sql: "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND OriginalInitiatorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_TenantId_OriginalInitiatorPrincipalId",
                table: "Activations",
                columns: new[] { "TenantId", "OriginalInitiatorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_TenantId_RootActivationId_ActivationId",
                table: "Activations",
                columns: new[] { "TenantId", "RootActivationId", "ActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "Activations",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activations_ExecutionOwner",
                table: "Activations",
                sql: "(TenantId IS NULL AND ExecutorPrincipalId IS NULL AND OriginalInitiatorPrincipalId IS NULL AND ProfileId IS NOT NULL) OR (TenantId IS NOT NULL AND ExecutorPrincipalId IS NOT NULL AND ProfileId IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Activations_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "Activations",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                principalTable: "TenantSessions",
                principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activations_TenantUsers_TenantId_OriginalInitiatorPrincipalId",
                table: "Activations",
                columns: new[] { "TenantId", "OriginalInitiatorPrincipalId" },
                principalTable: "TenantUsers",
                principalColumns: new[] { "TenantId", "PrincipalId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "AgentRuns",
                columns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                principalTable: "TenantSessions",
                principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_TenantUsers_TenantId_OriginalInitiatorPrincipalId",
                table: "AgentRuns",
                columns: new[] { "TenantId", "OriginalInitiatorPrincipalId" },
                principalTable: "TenantUsers",
                principalColumns: new[] { "TenantId", "PrincipalId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activations_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "Activations");

            migrationBuilder.DropForeignKey(
                name: "FK_Activations_TenantUsers_TenantId_OriginalInitiatorPrincipalId",
                table: "Activations");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_TenantUsers_TenantId_OriginalInitiatorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TenantSessions_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "TenantSessions");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_TenantId_OriginalInitiatorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_TenantId_SessionId_CreatedAtUtc_AgentRunId",
                table: "AgentRuns");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AgentRuns_ExecutionOwner",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_Activations_TenantId_OriginalInitiatorPrincipalId",
                table: "Activations");

            migrationBuilder.DropIndex(
                name: "IX_Activations_TenantId_RootActivationId_ActivationId",
                table: "Activations");

            migrationBuilder.DropIndex(
                name: "IX_Activations_TenantId_SessionId_AgentInstanceId_ExecutorPrincipalId",
                table: "Activations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Activations_ExecutionOwner",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "ExecutorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "OriginalInitiatorPrincipalId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "ExecutorPrincipalId",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "OriginalInitiatorPrincipalId",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Activations");

            migrationBuilder.AlterColumn<string>(
                name: "ProfileId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProfileId",
                table: "Activations",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
