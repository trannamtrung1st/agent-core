using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentInstance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentInstanceId",
                table: "Sessions",
                type: "TEXT",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinnedPersonaJson",
                table: "Sessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentInstances",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActiveVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonaJson = table.Column<string>(type: "TEXT", nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Compatibility = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstances", x => x.InstanceId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AgentInstanceId",
                table: "Sessions",
                column: "AgentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances",
                column: "DefinitionId",
                unique: true,
                filter: "Compatibility = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentInstances");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_AgentInstanceId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "AgentInstanceId",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "PinnedPersonaJson",
                table: "Sessions");
        }
    }
}
