using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDurableApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_AgentRuns_AgentRunId_RootActivationId",
                table: "AgentRuns",
                columns: new[] { "AgentRunId", "RootActivationId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_AgentRuns_AgentRunId_SessionId",
                table: "AgentRuns",
                columns: new[] { "AgentRunId", "SessionId" });

            migrationBuilder.CreateTable(
                name: "TenantApprovalRequests",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    RootActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginalInitiatorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", nullable: false),
                    Permission = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceType = table.Column<string>(type: "TEXT", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantApprovalRequests", x => x.RequestId);
                    table.UniqueConstraint("AK_TenantApprovalRequests_TenantId_RequestId", x => new { x.TenantId, x.RequestId });
                    table.UniqueConstraint("AK_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_Fingerprint", x => new { x.TenantId, x.RequestId, x.ExecutorPrincipalId, x.OperationId, x.Fingerprint });
                    table.CheckConstraint("CK_TenantApprovalRequest_Values", "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND length(PayloadJson) <= 131072");
                    table.ForeignKey(
                        name: "FK_TenantApprovalRequests_AgentRuns_RunId_RootActivationId",
                        columns: x => new { x.RunId, x.RootActivationId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "RootActivationId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantApprovalRequests_AgentRuns_RunId_SessionId",
                        columns: x => new { x.RunId, x.SessionId },
                        principalTable: "AgentRuns",
                        principalColumns: new[] { "AgentRunId", "SessionId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantApprovalRequests_TenantSessions_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                        columns: x => new { x.TenantId, x.SessionId, x.InstanceId, x.ExecutorPrincipalId },
                        principalTable: "TenantSessions",
                        principalColumns: new[] { "TenantId", "SessionId", "AgentInstanceId", "ExecutorPrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantApprovalRequests_TenantUsers_TenantId_OriginalInitiatorPrincipalId",
                        columns: x => new { x.TenantId, x.OriginalInitiatorPrincipalId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalProcessedSignals",
                columns: table => new
                {
                    SignalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DecisionRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProcessedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalProcessedSignals", x => x.SignalId);
                    table.ForeignKey(
                        name: "FK_ApprovalProcessedSignals_TenantApprovalRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "TenantId", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantApprovalDecisions",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    HumanPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    RequestRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantApprovalDecisions", x => x.DecisionId);
                    table.ForeignKey(
                        name: "FK_TenantApprovalDecisions_TenantApprovalRequests_TenantId_RequestId",
                        columns: x => new { x.TenantId, x.RequestId },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "TenantId", "RequestId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TenantApprovalDecisions_TenantUsers_TenantId_HumanPrincipalId",
                        columns: x => new { x.TenantId, x.HumanPrincipalId },
                        principalTable: "TenantUsers",
                        principalColumns: new[] { "TenantId", "PrincipalId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantOperationGrants",
                columns: table => new
                {
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExecutorPrincipalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OperationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    EffectIdempotencyKey = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOperationGrants", x => x.GrantId);
                    table.CheckConstraint("CK_TenantOperationGrant_Values", "Status IN (0,1,2,3,4) AND Revision >= 1 AND length(ActionHash) = 64");
                    table.ForeignKey(
                        name: "FK_TenantOperationGrants_TenantApprovalRequests_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                        columns: x => new { x.TenantId, x.RequestId, x.ExecutorPrincipalId, x.OperationId, x.ActionHash },
                        principalTable: "TenantApprovalRequests",
                        principalColumns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalProcessedSignals_ProcessedAt_CreatedAt",
                table: "ApprovalProcessedSignals",
                columns: new[] { "ProcessedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalProcessedSignals_RequestId",
                table: "ApprovalProcessedSignals",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalProcessedSignals_TenantId_RequestId",
                table: "ApprovalProcessedSignals",
                columns: new[] { "TenantId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalDecisions_RequestId",
                table: "TenantApprovalDecisions",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalDecisions_TenantId_HumanPrincipalId",
                table: "TenantApprovalDecisions",
                columns: new[] { "TenantId", "HumanPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalDecisions_TenantId_RequestId",
                table: "TenantApprovalDecisions",
                columns: new[] { "TenantId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_OperationId",
                table: "TenantApprovalRequests",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_RunId_RootActivationId",
                table: "TenantApprovalRequests",
                columns: new[] { "RunId", "RootActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_RunId_SessionId",
                table: "TenantApprovalRequests",
                columns: new[] { "RunId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_Status_ExpiresAt_RequestId",
                table: "TenantApprovalRequests",
                columns: new[] { "Status", "ExpiresAt", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_ExecutorPrincipalId_CreatedAt",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "ExecutorPrincipalId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_OriginalInitiatorPrincipalId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "OriginalInitiatorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_RootActivationId_ExecutorPrincipalId_Fingerprint",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "RootActivationId", "ExecutorPrincipalId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_SessionId_InstanceId_ExecutorPrincipalId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "SessionId", "InstanceId", "ExecutorPrincipalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_Status_CreatedAt_RequestId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "Status", "CreatedAt", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationGrants_EffectIdempotencyKey",
                table: "TenantOperationGrants",
                column: "EffectIdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationGrants_RequestId",
                table: "TenantOperationGrants",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationGrants_TenantId_RequestId_ExecutorPrincipalId_OperationId_ActionHash",
                table: "TenantOperationGrants",
                columns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "ActionHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApprovalProcessedSignals");

            migrationBuilder.DropTable(
                name: "TenantApprovalDecisions");

            migrationBuilder.DropTable(
                name: "TenantOperationGrants");

            migrationBuilder.DropTable(
                name: "TenantApprovalRequests");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AgentRuns_AgentRunId_RootActivationId",
                table: "AgentRuns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_AgentRuns_AgentRunId_SessionId",
                table: "AgentRuns");
        }
    }
}
