using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentialApprovalTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests");

            migrationBuilder.AddColumn<string>(
                name: "CredentialId",
                table: "TenantApprovalRequests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantApprovalRequests_TenantId_CredentialId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests",
                sql: "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND ((Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND CredentialId IS NULL) OR (Permission = 'credentials.use' AND ResourceType = 'credential' AND CredentialId IS NOT NULL AND ResourceId = CredentialId)) AND length(PayloadJson) <= 131072");

            migrationBuilder.AddForeignKey(
                name: "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId",
                table: "TenantApprovalRequests",
                columns: new[] { "TenantId", "CredentialId" },
                principalTable: "TenantCredentials",
                principalColumns: new[] { "TenantId", "CredentialId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE CredentialApprovalDowngradeGuard (Valid INTEGER NOT NULL CHECK (Valid = 1));
                INSERT INTO CredentialApprovalDowngradeGuard SELECT CASE WHEN EXISTS (SELECT 1 FROM TenantApprovalRequests WHERE CredentialId IS NOT NULL) THEN 0 ELSE 1 END;
                DROP TABLE CredentialApprovalDowngradeGuard;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_TenantApprovalRequests_TenantCredentials_TenantId_CredentialId",
                table: "TenantApprovalRequests");

            migrationBuilder.DropIndex(
                name: "IX_TenantApprovalRequests_TenantId_CredentialId",
                table: "TenantApprovalRequests");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests");

            migrationBuilder.DropColumn(
                name: "CredentialId",
                table: "TenantApprovalRequests");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantApprovalRequest_Values",
                table: "TenantApprovalRequests",
                sql: "Kind IN (0,1) AND Status IN (0,1,2,3,4) AND Revision >= 1 AND ExpiresAt > CreatedAt AND length(Fingerprint) = 64 AND Permission = 'agents.update' AND ResourceType = 'agent' AND ResourceId = InstanceId AND length(PayloadJson) <= 131072");
        }
    }
}
