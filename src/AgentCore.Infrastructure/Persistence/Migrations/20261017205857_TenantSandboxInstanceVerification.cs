using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSandboxInstanceVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications");

            migrationBuilder.AddColumn<bool>(
                name: "AcceptedFull",
                table: "TenantSandboxVerificationAttempts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "InstanceId",
                table: "TenantSandboxVerificationAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InstanceRevision",
                table: "TenantSandboxVerificationAttempts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "InstanceId" },
                unique: true,
                filter: "Active = 1 AND InstanceId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId_InstanceRevision_ProfileId_ProfileRevision_AcceptedFull_SecurityRevision",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "InstanceId", "InstanceRevision", "ProfileId", "ProfileRevision", "AcceptedFull", "SecurityRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId_ProfileId_SecurityRevision",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "InstanceId", "ProfileId", "SecurityRevision" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "ProfileId" },
                unique: true,
                filter: "Active = 1 AND InstanceId IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxAttempt_InstanceJson",
                table: "TenantSandboxVerificationAttempts",
                sql: "(InstanceId IS NULL AND InstanceRevision IS NULL AND json_extract(Json, '$.instanceId') IS NULL AND json_extract(Json, '$.instanceRevision') IS NULL) OR (InstanceId IS NOT NULL AND InstanceRevision IS NOT NULL AND InstanceRevision > 0 AND json_type(Json, '$.instanceId') IS 'text' AND lower(json_extract(Json, '$.instanceId')) = lower(InstanceId) AND json_type(Json, '$.instanceRevision') IS 'integer' AND json_extract(Json, '$.instanceRevision') = InstanceRevision)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications",
                sql: "((TenantId IS NULL AND AttemptId IS NULL AND json_extract(Json, '$.tenantId') IS NULL) OR (TenantId IS NOT NULL AND AttemptId IS NOT NULL AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId))) AND json_extract(Json, '$.instanceId') IS NULL AND json_extract(Json, '$.instanceRevision') IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_TenantSandboxVerificationAttempts_SystemIdentities_TenantId_InstanceId",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "InstanceId" },
                principalTable: "SystemIdentities",
                principalColumns: new[] { "TenantId", "AgentInstanceId" },
                onDelete: ReferentialAction.Restrict);

            // Existing reusable receipts keep their original accepted full-result semantics.
            migrationBuilder.Sql("UPDATE TenantSandboxVerificationAttempts SET AcceptedFull = CASE WHEN json_extract(Json, '$.imageOnly') = 0 AND json_extract(Json, '$.status') IN (1, 2) THEN 1 ELSE 0 END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TenantSandboxVerificationAttempts_SystemIdentities_TenantId_InstanceId",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId_InstanceRevision_ProfileId_ProfileRevision_AcceptedFull_SecurityRevision",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_InstanceId_ProfileId_SecurityRevision",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxAttempt_InstanceJson",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications");

            migrationBuilder.DropColumn(
                name: "AcceptedFull",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.DropColumn(
                name: "InstanceRevision",
                table: "TenantSandboxVerificationAttempts");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSandboxVerificationAttempts_TenantId_ProfileId",
                table: "TenantSandboxVerificationAttempts",
                columns: new[] { "TenantId", "ProfileId" },
                unique: true,
                filter: "Active = 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxVerification_TenantJson",
                table: "SandboxProfileVerifications",
                sql: "(TenantId IS NULL AND AttemptId IS NULL AND json_extract(Json, '$.tenantId') IS NULL) OR (TenantId IS NOT NULL AND AttemptId IS NOT NULL AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId))");
        }
    }
}
