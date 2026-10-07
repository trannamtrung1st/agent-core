using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UnifiedAgentWorkspace : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Pre-release compatibility/null-owner data is deliberately rejected, never backfilled.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE WorkspaceMigrationGate (Invalid INTEGER NOT NULL);
                CREATE TEMP TRIGGER RequireWorkspaceDataReset BEFORE INSERT ON WorkspaceMigrationGate
                WHEN NEW.Invalid = 1 BEGIN
                    SELECT RAISE(ABORT, 'Legacy data reset required: reset the Agent Core database and data roots before starting this version.');
                END;
                INSERT INTO WorkspaceMigrationGate SELECT
                    EXISTS(SELECT 1 FROM AgentInstances WHERE Compatibility = 1) OR
                    EXISTS(SELECT 1 FROM Sessions WHERE AgentInstanceId IS NULL OR AgentInstanceId = '' OR AgentInstanceId = '00000000-0000-0000-0000-000000000000');
                DROP TRIGGER RequireWorkspaceDataReset;
                DROP TABLE WorkspaceMigrationGate;
                """);

            migrationBuilder.DropIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances");

            migrationBuilder.DropColumn(
                name: "Compatibility",
                table: "AgentInstances");

            migrationBuilder.AlterColumn<string>(
                name: "AgentInstanceId",
                table: "Sessions",
                type: "TEXT",
                maxLength: 36,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 36,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances",
                column: "DefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances");

            migrationBuilder.AlterColumn<string>(
                name: "AgentInstanceId",
                table: "Sessions",
                type: "TEXT",
                maxLength: 36,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 36);

            migrationBuilder.AddColumn<bool>(
                name: "Compatibility",
                table: "AgentInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances",
                column: "DefinitionId",
                unique: true,
                filter: "Compatibility = 1");
        }
    }
}
