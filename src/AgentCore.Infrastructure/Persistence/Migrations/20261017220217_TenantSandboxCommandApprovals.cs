using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSandboxCommandApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests");

            migrationBuilder.AddColumn<string>(
                name: "SandboxProfileId",
                table: "TenantApprovalRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SandboxProfileRevision",
                table: "TenantApprovalRequests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovalRequestId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedActionHash",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "SandboxExecutions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision",
                table: "TenantApprovalRequests",
                columns: new[] { "SandboxProfileId", "SandboxProfileRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_SandboxProfileId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "SandboxProfileId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests",
                sql: "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId AND SandboxProfileId IS NULL AND SandboxProfileRevision IS NULL AND length(PayloadJson) <= 131072) OR (Permission = 'sandbox.execute' AND Kind = 0 AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL AND SandboxProfileId IS NOT NULL AND SandboxProfileRevision IS NOT NULL AND SandboxProfileRevision > 0 AND json_type(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS 'text' AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileId') IS SandboxProfileId AND json_extract(PayloadJson, '$.Operation.SandboxCommand.ProfileRevision') IS SandboxProfileRevision AND length(PayloadJson) <= 1048576))");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_OperationId",
                table: "SandboxExecutions",
                column: "OperationId",
                unique: true,
                filter: "OperationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_TenantId_ApprovalRequestId_ExecutorPrincipalId_OperationId_ApprovedActionHash",
                table: "SandboxExecutions",
                columns: new[] { "TenantId", "ApprovalRequestId", "ExecutorPrincipalId", "OperationId", "ApprovedActionHash" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxExecution_Approval",
                table: "SandboxExecutions",
                sql: "(ApprovalRequestId IS NULL AND OperationId IS NULL AND ApprovedActionHash IS NULL AND json_extract(Json, '$.approvalRequestId') IS NULL AND json_extract(Json, '$.operationId') IS NULL) OR (TenantId IS NOT NULL AND ApprovalRequestId IS NOT NULL AND OperationId IS NOT NULL AND ApprovedActionHash IS NOT NULL AND json_type(Json, '$.approvalRequestId') IS 'text' AND lower(json_extract(Json, '$.approvalRequestId')) = lower(ApprovalRequestId) AND json_type(Json, '$.operationId') IS 'text' AND lower(json_extract(Json, '$.operationId')) = lower(OperationId) AND json_extract(Json, '$.actionHash') IS ApprovedActionHash)");

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxExecutions_TenantApprovalRequests_TenantId_ApprovalRequestId_ExecutorPrincipalId_OperationId_ApprovedActionHash",
                table: "SandboxExecutions",
                columns: new[] { "TenantId", "ApprovalRequestId", "ExecutorPrincipalId", "OperationId", "ApprovedActionHash" },
                principalTable: "TenantApprovalRequests",
                principalColumns: new[] { "TenantId", "RequestId", "ExecutorPrincipalId", "OperationId", "Fingerprint" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision",
                table: "TenantApprovalRequests",
                columns: new[] { "SandboxProfileId", "SandboxProfileRevision" },
                principalTable: "SandboxProfiles",
                principalColumns: new[] { "Id", "Revision" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "SandboxProfileId" },
                principalTable: "TenantSandboxProfiles",
                principalColumns: new[] { "TenantId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SandboxExecutions_TenantApprovalRequests_TenantId_ApprovalRequestId_ExecutorPrincipalId_OperationId_ApprovedActionHash",
                table: "SandboxExecutions");

            migrationBuilder.DropForeignKey(
                name: "FK_TenantApprovalRequests_SandboxProfiles_SandboxProfileId_SandboxProfileRevision",
                table: "TenantApprovalRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_TenantApprovalRequests_TenantSandboxProfiles_TenantId_SandboxProfileId",
                table: "TenantApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_TenantApprovalRequests_SandboxProfileId_SandboxProfileRevision",
                table: "TenantApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_TenantApprovalRequests_TenantId_SandboxProfileId",
                table: "TenantApprovalRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_OperationId",
                table: "SandboxExecutions");

            migrationBuilder.DropIndex(
                name: "IX_SandboxExecutions_TenantId_ApprovalRequestId_ExecutorPrincipalId_OperationId_ApprovedActionHash",
                table: "SandboxExecutions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxExecution_Approval",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "SandboxProfileId",
                table: "TenantApprovalRequests");

            migrationBuilder.DropColumn(
                name: "SandboxProfileRevision",
                table: "TenantApprovalRequests");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestId",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "ApprovedActionHash",
                table: "SandboxExecutions");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "SandboxExecutions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests",
                sql: "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId)) AND length(PayloadJson) <= 131072");
        }
    }
}
