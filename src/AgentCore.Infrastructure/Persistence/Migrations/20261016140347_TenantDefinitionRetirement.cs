using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantDefinitionRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeletedAt",
                table: "TenantAgentDefinitions",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                table: "TenantAgentDefinitions");
        }
    }
}
