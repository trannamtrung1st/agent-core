using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapabilityExecutionLoads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CapabilityLoadCount",
                table: "ConversationTurnExecutions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LoadedCapabilityIdsJson",
                table: "ConversationTurnExecutions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CapabilityLoadCount",
                table: "ConversationTurnExecutions");

            migrationBuilder.DropColumn(
                name: "LoadedCapabilityIdsJson",
                table: "ConversationTurnExecutions");
        }
    }
}
