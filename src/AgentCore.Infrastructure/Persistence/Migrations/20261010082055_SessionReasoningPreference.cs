using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SessionReasoningPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ModelHasExplicitReasoningEffort",
                table: "SessionSnapshots",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Historical snapshots do not distinguish an explicitly selected effort from a default.
            // Retain their concrete choice conservatively rather than erase a user preference.
            migrationBuilder.Sql("UPDATE SessionSnapshots SET ModelHasExplicitReasoningEffort = 1 WHERE ModelReasoningEffort IS NOT NULL AND trim(ModelReasoningEffort) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Scoped configuration preference rollback requires restoring a pre-cutover backup.");
        }
    }
}
