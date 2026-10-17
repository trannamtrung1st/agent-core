using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantOperationPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantOperationPolicies",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    IndependentReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantOperationPolicies", x => new { x.TenantId, x.PermissionKey });
                    table.CheckConstraint("CK_TenantOperationPolicies_Values", "Mode IN (0,1,2) AND IndependentReview IN (0,1) AND Revision >= 1 AND length(PermissionKey) BETWEEN 1 AND 128");
                    table.ForeignKey(
                        name: "FK_TenantOperationPolicies_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "TenantId",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantOperationPolicies");
        }
    }
}
