using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UntouchedSkillInheritance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Also repair databases that already applied the original materialized-boolean cutover.
            // Initialized Skills start at revision 1; ordinary explicit writes advance that revision.
            migrationBuilder.Sql("UPDATE AgentDefinitionSkillStates SET EnabledOverride = NULL WHERE Revision = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => throw new System.NotSupportedException(
            "Untouched Skill inheritance cannot recover discarded initialized booleans. Restore the pre-upgrade backup.");
    }
}
