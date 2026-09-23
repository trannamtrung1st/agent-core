using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerScopedOccurrenceDedupe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_DedupeKey",
                table: "TriggerOccurrences");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey",
                table: "TriggerOccurrences",
                columns: new[] { "AgentInstanceId", "ProfileId", "DedupeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey",
                table: "TriggerOccurrences");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_DedupeKey",
                table: "TriggerOccurrences",
                column: "DedupeKey",
                unique: true);
        }
    }
}
