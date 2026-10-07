using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstanceSkillsCutover : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PinnedActiveSkillIdsJson",
                table: "ConversationTurnExecutions");

            migrationBuilder.AddColumn<string>(
                name: "ActiveSkillKeysJson",
                table: "ConversationTurnExecutions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PinnedSkillCatalogJson",
                table: "ConversationTurnExecutions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AgentDefinitionSkillStates",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionSkillId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionSkillStates", x => new { x.AgentInstanceId, x.DefinitionSkillId });
                    table.ForeignKey(
                        name: "FK_AgentDefinitionSkillStates_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentInstanceSkills",
                columns: table => new
                {
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Procedure = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Projection = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDefinitionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceDefinitionVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceDefinitionSkillId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstanceSkills", x => x.SkillId);
                    table.ForeignKey(
                        name: "FK_AgentInstanceSkills_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionSkillStates_AgentInstanceId",
                table: "AgentDefinitionSkillStates",
                column: "AgentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstanceSkills_AgentInstanceId_UpdatedAtUtc",
                table: "AgentInstanceSkills",
                columns: new[] { "AgentInstanceId", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitionSkillStates");

            migrationBuilder.DropTable(
                name: "AgentInstanceSkills");

            migrationBuilder.DropColumn(
                name: "ActiveSkillKeysJson",
                table: "ConversationTurnExecutions");

            migrationBuilder.DropColumn(
                name: "PinnedSkillCatalogJson",
                table: "ConversationTurnExecutions");

            migrationBuilder.AddColumn<string>(
                name: "PinnedActiveSkillIdsJson",
                table: "ConversationTurnExecutions",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }
    }
}
