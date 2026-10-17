using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantExecutionAuthorizationAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionJson",
                table: "AuthorizationAudit",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuthorizationAudit_Execution",
                table: "AuthorizationAudit",
                sql: "ExecutionJson IS NULL OR (length(ExecutionJson) <= 4096 AND json_valid(ExecutionJson))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AuthorizationAudit_Execution",
                table: "AuthorizationAudit");

            migrationBuilder.DropColumn(
                name: "ExecutionJson",
                table: "AuthorizationAudit");
        }
    }
}
