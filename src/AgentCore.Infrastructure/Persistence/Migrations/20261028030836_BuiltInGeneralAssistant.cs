using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BuiltInGeneralAssistant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ManagedKind",
                table: "SystemIdentities",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PersonaCustomized",
                table: "AgentInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "ShowInChat",
                table: "AgentInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemIdentities_TenantId_ManagedKind",
                table: "SystemIdentities",
                columns: new[] { "TenantId", "ManagedKind" },
                unique: true,
                filter: "ManagedKind IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite's provider rebuild for DropColumn invalidates retained cross-table
            // decommission triggers. These unreferenced additive columns support native
            // DROP COLUMN after removing the sole new index; keep all existing guards.
            migrationBuilder.Sql("""
                DROP INDEX IX_SystemIdentities_TenantId_ManagedKind;
                ALTER TABLE SystemIdentities DROP COLUMN ManagedKind;
                ALTER TABLE AgentInstances DROP COLUMN PersonaCustomized;
                ALTER TABLE AgentInstances DROP COLUMN ShowInChat;
                """);
        }
    }
}
