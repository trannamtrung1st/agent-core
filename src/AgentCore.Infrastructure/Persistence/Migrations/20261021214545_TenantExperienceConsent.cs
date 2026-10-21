using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

// Frozen approval table and every affected guard. Automatic SQLite rebuilds
// discard triggers; later helpers must not revise this migration's history.
public partial class TenantExperienceConsent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => Rebuild(migrationBuilder, Upgrade);
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE ExperienceConsentDowngradeGuard (Rows INTEGER CHECK(Rows=0));
            INSERT INTO ExperienceConsentDowngradeGuard SELECT count(*) FROM TenantApprovalRequests
                WHERE json_extract(PayloadJson,'$.Operation.ExperienceOperation') IS NOT NULL OR json_extract(PayloadJson,'$.Operation.ContinuityRead.Experience') IS NOT NULL;
            DROP TABLE ExperienceConsentDowngradeGuard;
            """);
        Rebuild(migrationBuilder, Downgrade);
    }
    private static void Rebuild(MigrationBuilder migrationBuilder, string sql)
    {
        migrationBuilder.Sql("PRAGMA foreign_keys = 0;", suppressTransaction: true);
        migrationBuilder.Sql(sql);
        migrationBuilder.Sql("PRAGMA foreign_keys = 1;", suppressTransaction: true);
    }
    private static string Upgrade => Read("Up");
    private static string Downgrade => Read("Down");
    private static string Read(string direction)
    {
        using var stream = typeof(TenantExperienceConsent).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261021214545_TenantExperienceConsent." + direction + ".sql")
            ?? throw new InvalidOperationException("Frozen Experience consent migration SQL is missing.");
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
