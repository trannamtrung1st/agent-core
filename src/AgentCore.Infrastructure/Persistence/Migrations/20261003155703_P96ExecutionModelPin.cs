using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P96ExecutionModelPin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ModelOverrideCatalogKey",
                table: "TriggerRegistrations",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelOverrideReasoningEffort",
                table: "TriggerRegistrations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequiresVision",
                table: "TriggerRegistrations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ModelCatalogKey",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelId",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelProviderAlias",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelReasoningEffort",
                table: "TriggerOccurrences",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ModelSource",
                table: "TriggerOccurrences",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnattendedModelCatalogKey",
                table: "AgentInstances",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnattendedReasoningEffort",
                table: "AgentInstances",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ModelOverrideCatalogKey",
                table: "TriggerRegistrations");

            migrationBuilder.DropColumn(
                name: "ModelOverrideReasoningEffort",
                table: "TriggerRegistrations");

            migrationBuilder.DropColumn(
                name: "RequiresVision",
                table: "TriggerRegistrations");

            migrationBuilder.DropColumn(
                name: "ModelCatalogKey",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ModelId",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ModelProviderAlias",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ModelReasoningEffort",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "ModelSource",
                table: "TriggerOccurrences");

            migrationBuilder.DropColumn(
                name: "UnattendedModelCatalogKey",
                table: "AgentInstances");

            migrationBuilder.DropColumn(
                name: "UnattendedReasoningEffort",
                table: "AgentInstances");
        }
    }
}
