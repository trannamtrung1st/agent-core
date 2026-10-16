using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UniqueAgentTenantOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SystemIdentities_TenantId_AgentInstanceId",
                table: "SystemIdentities");

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_AgentInstanceId",
                table: "SystemIdentities",
                column: "AgentInstanceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_TenantId",
                table: "SystemIdentities",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SystemIdentities_AgentInstanceId",
                table: "SystemIdentities");

            migrationBuilder.DropIndex(
                name: "IX_SystemIdentities_TenantId",
                table: "SystemIdentities");

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_TenantId_AgentInstanceId",
                table: "SystemIdentities",
                columns: new[] { "TenantId", "AgentInstanceId" },
                unique: true);
        }
    }
}
