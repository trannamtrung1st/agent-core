using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P7WorkPinnedPersona : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PinnedPersonaJson",
                table: "WorkItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PinnedPersonaJson",
                table: "WorkItems");
        }
    }
}
