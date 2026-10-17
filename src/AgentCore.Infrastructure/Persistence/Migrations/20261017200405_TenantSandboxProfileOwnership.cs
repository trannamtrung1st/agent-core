using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantSandboxProfileOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "SandboxProfiles",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SandboxProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantSandboxProfiles",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantSandboxProfiles", x => x.ProfileId);
                    table.UniqueConstraint("AK_TenantSandboxProfiles_TenantId_ProfileId", x => new { x.TenantId, x.ProfileId });
                    table.ForeignKey(
                        name: "FK_TenantSandboxProfiles_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SandboxProfiles_TenantId_Id_Revision",
                table: "SandboxProfiles",
                columns: new[] { "TenantId", "Id", "Revision" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SandboxProfiles_TenantJson",
                table: "SandboxProfiles",
                sql: "(TenantId IS NULL AND json_extract(Json, '$.tenantId') IS NULL) OR (TenantId IS NOT NULL AND json_type(Json, '$.tenantId') IS 'text' AND lower(json_extract(Json, '$.tenantId')) = lower(TenantId))");

            migrationBuilder.AddForeignKey(
                name: "FK_SandboxProfiles_TenantSandboxProfiles_TenantId_Id",
                table: "SandboxProfiles",
                columns: new[] { "TenantId", "Id" },
                principalTable: "TenantSandboxProfiles",
                principalColumns: new[] { "TenantId", "ProfileId" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SandboxProfiles_TenantSandboxProfiles_TenantId_Id",
                table: "SandboxProfiles");

            migrationBuilder.DropTable(
                name: "TenantSandboxProfiles");

            migrationBuilder.DropIndex(
                name: "IX_SandboxProfiles_TenantId_Id_Revision",
                table: "SandboxProfiles");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SandboxProfiles_TenantJson",
                table: "SandboxProfiles");

            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "SandboxProfiles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SandboxProfiles");
        }
    }
}
