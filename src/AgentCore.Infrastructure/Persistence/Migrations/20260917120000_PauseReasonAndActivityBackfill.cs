using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

public partial class PauseReasonAndActivityBackfill : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PauseReason",
            table: "Sessions",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql(
            """
            UPDATE SessionSnapshots
            SET LastUserActivityAtUtc = COALESCE(LastUserActivityAtUtc, UpdatedAtUtc)
            WHERE LastUserActivityAtUtc IS NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "PauseReason",
            table: "Sessions");
    }
}
