using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDirectLocalOperationReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "RequestId",
                table: "TenantOperationReceipts",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "ParametersHash",
                table: "TenantOperationReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCallId",
                table: "TenantOperationReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantOperationReceipts_TenantId_RunId_SourceCallId",
                table: "TenantOperationReceipts",
                columns: new[] { "TenantId", "RunId", "SourceCallId" },
                unique: true,
                filter: "SourceCallId IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_TenantOperationReceipt_Source",
                table: "TenantOperationReceipts",
                sql: "(RequestId IS NOT NULL AND SourceCallId IS NULL AND ParametersHash IS NULL) OR (RequestId IS NULL AND SourceCallId IS NOT NULL AND length(SourceCallId) BETWEEN 1 AND 200 AND ParametersHash IS NOT NULL AND length(ParametersHash) = 64)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous schema cannot represent direct receipts. Refuse a lossy downgrade.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE DirectReceiptDowngradeGuard (Valid INTEGER NOT NULL CHECK (Valid = 1));
                INSERT INTO DirectReceiptDowngradeGuard SELECT 0 FROM TenantOperationReceipts WHERE RequestId IS NULL LIMIT 1;
                DROP TABLE DirectReceiptDowngradeGuard;
                """);

            migrationBuilder.DropIndex(
                name: "IX_TenantOperationReceipts_TenantId_RunId_SourceCallId",
                table: "TenantOperationReceipts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TenantOperationReceipt_Source",
                table: "TenantOperationReceipts");

            migrationBuilder.DropColumn(
                name: "ParametersHash",
                table: "TenantOperationReceipts");

            migrationBuilder.DropColumn(
                name: "SourceCallId",
                table: "TenantOperationReceipts");

            migrationBuilder.AlterColumn<Guid>(
                name: "RequestId",
                table: "TenantOperationReceipts",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
