using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantApprovedLocalOperationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId",
                table: "TenantApprovalRequests",
                columns: new[] { "RequestId", "RootActivationId", "InstanceId" });

            migrationBuilder.CreateTable(
                name: "TenantOperationReceipts",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    RootActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    Outcome = table.Column<int>(type: "INTEGER", nullable: false),
                    ReasonCode = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonaRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOperationReceipts", x => x.OperationId);
                    table.CheckConstraint("CK_TenantOperationReceipt_Values", "Outcome IN (0,1) AND InstanceRevision >= 1 AND PersonaRevision >= 1 AND length(ActionHash) = 64 AND length(ReasonCode) BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_TenantOperationReceipts_AgentRuns_RunId_RootActivationId",
                        columns: x => new { x.RunId, x.RootActivationId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "RootActivationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOperationReceipts_AgentRuns_RunId_SessionId",
                        columns: x => new { x.RunId, x.SessionId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOperationReceipts_TenantApprovalRequests_RequestId_RootActivationId_InstanceId",
                        columns: x => new { x.RequestId, x.RootActivationId, x.InstanceId },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "RequestId", "RootActivationId", "InstanceId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOperationReceipts_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                        columns: x => new { x.TenantId, x.RequestId, x.ExecutorPrincipalId, x.OperationId, x.ActionHash },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantOperationReceipts_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.SessionId, x.InstanceId, x.ExecutorPrincipalId },
                        principalTable: "TenantSessions",
                        principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_RequestId",
                table: "TenantOperationReceipts",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_RequestId_RootActivationId_InstanceId",
                table: "TenantOperationReceipts",
                columns: new[] { "RequestId", "RootActivationId", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_RunId_RootActivationId",
                table: "TenantOperationReceipts",
                columns: new[] { "RunId", "RootActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_RunId_SessionId",
                table: "TenantOperationReceipts",
                columns: new[] { "RunId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                table: "TenantOperationReceipts",
                columns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "ActionHash" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_TenantId_RunId_RecordedAt",
                table: "TenantOperationReceipts",
                columns: new[] { "TenantId", "RunId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                table: "TenantOperationReceipts",
                columns: new[] { "TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantOperationReceipts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TenantApprovalRequests_RequestId_RootActivationId_InstanceId",
                table: "TenantApprovalRequests");
        }
    }
}
