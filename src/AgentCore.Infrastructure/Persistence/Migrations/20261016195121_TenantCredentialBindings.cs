using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantCredentialBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite validates referenced key uniqueness while EF copies the rebuilt child.
            // Keep those witnesses available before EF rebuilds the owning parent tables;
            // rebuilding the parents replaces these temporary indexes with alternate keys.
            migrationBuilder.Sql("CREATE UNIQUE INDEX IX_BindingIdentityRebuildWitness ON SystemIdentities (TenantId, AgentInstanceId);");
            migrationBuilder.Sql("CREATE UNIQUE INDEX IX_BindingCredentialRebuildWitness ON TenantCredentials (TenantId, CredentialId);");
            migrationBuilder.DropIndex(
                name: "IX_TenantCredentials_TenantId_CredentialId",
                table: "TenantCredentials");

            migrationBuilder.DropIndex(
                name: "IX_SystemIdentities_TenantId",
                table: "SystemIdentities");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SystemIdentities",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AgentInstanceId",
                table: "SystemIdentities",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AgentCredentialBindings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TenantCredentials_TenantId_CredentialId",
                table: "TenantCredentials",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_SystemIdentities_TenantId_AgentInstanceId",
                table: "SystemIdentities",
                columns: new[] { "TenantId", "AgentInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_TenantId_AgentInstanceId",
                table: "AgentCredentialBindings",
                columns: new[] { "TenantId", "AgentInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_TenantId_CredentialId",
                table: "AgentCredentialBindings",
                columns: new[] { "TenantId", "CredentialId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentCredentialBindings_SystemIdentities_TenantId_AgentInstanceId",
                table: "AgentCredentialBindings",
                columns: new[] { "TenantId", "AgentInstanceId" },
                principalTable: "SystemIdentities",
                principalColumns: new[] { "TenantId", "AgentInstanceId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentCredentialBindings_TenantCredentials_TenantId_CredentialId",
                table: "AgentCredentialBindings",
                columns: new[] { "TenantId", "CredentialId" },
                principalTable: "TenantCredentials",
                principalColumns: new[] { "TenantId", "CredentialId" },
                onDelete: ReferentialAction.Restrict);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentCredentialBindings_SystemIdentities_TenantId_AgentInstanceId",
                table: "AgentCredentialBindings");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentCredentialBindings_TenantCredentials_TenantId_CredentialId",
                table: "AgentCredentialBindings");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TenantCredentials_TenantId_CredentialId",
                table: "TenantCredentials");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SystemIdentities_TenantId_AgentInstanceId",
                table: "SystemIdentities");

            migrationBuilder.DropIndex(
                name: "IX_AgentCredentialBindings_TenantId_AgentInstanceId",
                table: "AgentCredentialBindings");

            migrationBuilder.DropIndex(
                name: "IX_AgentCredentialBindings_TenantId_CredentialId",
                table: "AgentCredentialBindings");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AgentCredentialBindings");

            migrationBuilder.AlterColumn<Guid>(
                name: "TenantId",
                table: "SystemIdentities",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "AgentInstanceId",
                table: "SystemIdentities",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_TenantCredentials_TenantId_CredentialId",
                table: "TenantCredentials",
                columns: new[] { "TenantId", "CredentialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_TenantId",
                table: "SystemIdentities",
                column: "TenantId");
        }
    }
}
