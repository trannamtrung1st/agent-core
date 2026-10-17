using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ActivationRootLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Historical execution has no trustworthy root. Never infer or rewrite user lineage.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ActivationRootResetGuard (
                    EmptyExecutionGraph INTEGER CONSTRAINT Execution_history_requires_explicit_reset_before_root_lineage CHECK (EmptyExecutionGraph = 1));
                INSERT INTO ActivationRootResetGuard SELECT CASE WHEN EXISTS (SELECT 1 FROM Activations) OR EXISTS (SELECT 1 FROM AgentRuns) THEN 0 ELSE 1 END;
                DROP TABLE ActivationRootResetGuard;
                """);

            // SQLite checks the old principal table while EF copies the rebuilt self-referencing table.
            // Install the referenced columns/key first; the temporary key disappears with that old table.
            migrationBuilder.Sql("""
                ALTER TABLE Activations ADD COLUMN ParentActivationId TEXT NULL;
                ALTER TABLE Activations ADD COLUMN RootActivationId TEXT NOT NULL DEFAULT '';
                CREATE UNIQUE INDEX ActivationRootMigrationKey ON Activations (ActivationId, RootActivationId);
                """);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Activations_ActivationId_RootActivationId",
                table: "Activations",
                columns: new[] { "ActivationId", "RootActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_ParentActivationId_RootActivationId",
                table: "Activations",
                columns: new[] { "ParentActivationId", "RootActivationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Activations_RootActivationId",
                table: "Activations",
                column: "RootActivationId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activations_Lineage",
                table: "Activations",
                sql: "length(RootActivationId) = 36 AND (ParentActivationId IS NOT NULL OR RootActivationId = ActivationId) AND (ParentActivationId IS NULL OR (ParentActivationId <> ActivationId AND RootActivationId <> ActivationId))");

            migrationBuilder.AddForeignKey(
                name: "FK_Activations_Activations_ParentActivationId_RootActivationId",
                table: "Activations",
                columns: new[] { "ParentActivationId", "RootActivationId" },
                principalTable: "Activations",
                principalColumns: new[] { "ActivationId", "RootActivationId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activations_Activations_RootActivationId",
                table: "Activations",
                column: "RootActivationId",
                principalTable: "Activations",
                principalColumn: "ActivationId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_Activations_ActivationId",
                table: "AgentRuns");

            migrationBuilder.AddColumn<string>(
                name: "RootActivationId",
                table: "AgentRuns",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_ActivationId_RootActivationId",
                table: "AgentRuns",
                columns: new[] { "ActivationId", "RootActivationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_RootActivationId_CreatedAtUtc_AgentRunId",
                table: "AgentRuns",
                columns: new[] { "RootActivationId", "CreatedAtUtc", "AgentRunId" });

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_Activations_ActivationId_RootActivationId",
                table: "AgentRuns",
                columns: new[] { "ActivationId", "RootActivationId" },
                principalTable: "Activations",
                principalColumns: new[] { "ActivationId", "RootActivationId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_Activations_RootActivationId",
                table: "AgentRuns",
                column: "RootActivationId",
                principalTable: "Activations",
                principalColumn: "ActivationId",
                onDelete: ReferentialAction.Restrict);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Activations_Activations_ParentActivationId_RootActivationId",
                table: "Activations");

            migrationBuilder.DropForeignKey(
                name: "FK_Activations_Activations_RootActivationId",
                table: "Activations");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_Activations_ActivationId_RootActivationId",
                table: "AgentRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_Activations_RootActivationId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_ActivationId_RootActivationId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_RootActivationId_CreatedAtUtc_AgentRunId",
                table: "AgentRuns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Activations_ActivationId_RootActivationId",
                table: "Activations");

            migrationBuilder.DropIndex(
                name: "IX_Activations_ParentActivationId_RootActivationId",
                table: "Activations");

            migrationBuilder.DropIndex(
                name: "IX_Activations_RootActivationId",
                table: "Activations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Activations_Lineage",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "RootActivationId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "ParentActivationId",
                table: "Activations");

            migrationBuilder.DropColumn(
                name: "RootActivationId",
                table: "Activations");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_Activations_ActivationId",
                table: "AgentRuns",
                column: "ActivationId",
                principalTable: "Activations",
                principalColumn: "ActivationId",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
