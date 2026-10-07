using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InstanceSkillOwnerIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AgentInstanceSkills",
                table: "AgentInstanceSkills");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AgentInstanceSkills",
                table: "AgentInstanceSkills",
                columns: new[] { "AgentInstanceId", "SkillId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AgentInstanceSkills",
                table: "AgentInstanceSkills");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AgentInstanceSkills",
                table: "AgentInstanceSkills",
                column: "SkillId");
        }
    }
}
