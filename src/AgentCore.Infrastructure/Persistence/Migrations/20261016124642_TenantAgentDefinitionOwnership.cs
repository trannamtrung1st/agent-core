using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantAgentDefinitionOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantAgentDefinitions",
                columns: table => new
                {
                    DefinitionId = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAgentDefinitions", x => x.DefinitionId);
                    table.ForeignKey(
                        name: "FK_TenantAgentDefinitions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAgentDefinitions_TenantId_DefinitionId",
                table: "TenantAgentDefinitions",
                columns: new[] { "TenantId", "DefinitionId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAgentDefinitions");
        }
    }
}
