using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentIdentityBackingInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_SystemIdentities_AgentInstances_AgentInstanceId",
                table: "SystemIdentities",
                column: "AgentInstanceId",
                principalTable: "AgentInstances",
                principalColumn: "InstanceId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SystemIdentities_AgentInstances_AgentInstanceId",
                table: "SystemIdentities");
        }
    }
}
