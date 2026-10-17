using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentialCheckDispatchFences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantOperationReceipt_Values",
                table: "TenantOperationReceipts");

            migrationBuilder.CreateTable(
                name: "TenantCredentialCheckDispatches",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RootActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    Generation = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    StartedRunRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCredentialCheckDispatches", x => x.OperationId);
                    table.CheckConstraint("CK_TenantCredentialCheckDispatch_Values", "StartedRunRevision >= 1 AND length(ActionHash) = 64 AND (CompletedAt IS NULL OR CompletedAt >= StartedAt)");
                    table.ForeignKey(
                        name: "FK_TenantCredentialCheckDispatches_AgentRuns_RunId_RootActivationId",
                        columns: x => new { x.RunId, x.RootActivationId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "RootActivationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCredentialCheckDispatches_AgentRuns_RunId_SessionId",
                        columns: x => new { x.RunId, x.SessionId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCredentialCheckDispatches_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                        columns: x => new { x.TenantId, x.RequestId, x.ExecutorPrincipalId, x.OperationId, x.ActionHash },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCredentialCheckDispatches_TenantCredentials_TenantId_CredentialId",
                        columns: x => new { x.TenantId, x.CredentialId },
                        principalTable: "TenantCredentials",
                        principalColumns: new[] { "TenantId", "CredentialId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantCredentialCheckDispatches_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.SessionId, x.InstanceId, x.ExecutorPrincipalId },
                        principalTable: "TenantSessions",
                        principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantOperationReceipt_Values",
                table: "TenantOperationReceipts",
                sql: "Outcome IN (0,1,2) AND InstanceRevision >= 1 AND PersonaRevision >= 1 AND length(ActionHash) = 64 AND length(ReasonCode) BETWEEN 1 AND 128");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_CompletedAt_StartedAt",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "CompletedAt", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_RunId_RootActivationId",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "RunId", "RootActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_RunId_SessionId",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "RunId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_TenantId_CredentialId",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "TenantId", "CredentialId" },
                unique: true,
                filter: "CompletedAt IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "ActionHash" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentialCheckDispatches_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                table: "TenantCredentialCheckDispatches",
                columns: new[] { "TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE CredentialDispatchDowngradeGuard (Valid INTEGER NOT NULL CHECK (Valid = 1));
                INSERT INTO CredentialDispatchDowngradeGuard SELECT CASE WHEN EXISTS (SELECT 1 FROM TenantCredentialCheckDispatches) OR EXISTS (SELECT 1 FROM TenantOperationReceipts WHERE Outcome = 2) THEN 0 ELSE 1 END;
                DROP TABLE CredentialDispatchDowngradeGuard;
                """);
            migrationBuilder.DropTable(
                name: "TenantCredentialCheckDispatches");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantOperationReceipt_Values",
                table: "TenantOperationReceipts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantOperationReceipt_Values",
                table: "TenantOperationReceipts",
                sql: "Outcome IN (0,1) AND InstanceRevision >= 1 AND PersonaRevision >= 1 AND length(ActionHash) = 64 AND length(ReasonCode) BETWEEN 1 AND 128");
        }
    }
}
