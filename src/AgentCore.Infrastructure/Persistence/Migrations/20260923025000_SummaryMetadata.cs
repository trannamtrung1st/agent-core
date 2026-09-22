using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20260923025000_SummaryMetadata")]
public partial class SummaryMetadata : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SummaryFormatVersion",
            table: "SessionSnapshots",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<long>(
            name: "SummaryGeneratedAtUtc",
            table: "SessionSnapshots",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SummaryModelCatalogKey",
            table: "SessionSnapshots",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SummaryModelProviderAlias",
            table: "SessionSnapshots",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SummaryModelId",
            table: "SessionSnapshots",
            type: "TEXT",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "SummaryModelReasoningEffort",
            table: "SessionSnapshots",
            type: "TEXT",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "SummaryModelReasoningEffort", table: "SessionSnapshots");
        migrationBuilder.DropColumn(name: "SummaryModelId", table: "SessionSnapshots");
        migrationBuilder.DropColumn(name: "SummaryModelProviderAlias", table: "SessionSnapshots");
        migrationBuilder.DropColumn(name: "SummaryModelCatalogKey", table: "SessionSnapshots");
        migrationBuilder.DropColumn(name: "SummaryGeneratedAtUtc", table: "SessionSnapshots");
        migrationBuilder.DropColumn(name: "SummaryFormatVersion", table: "SessionSnapshots");
    }
}
