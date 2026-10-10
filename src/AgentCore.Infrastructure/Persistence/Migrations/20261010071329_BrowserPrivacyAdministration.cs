using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AgentCore.Infrastructure.Persistence.Migrations;
public partial class BrowserPrivacyAdministration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateTable(
        name: "BrowserPrivacy",
        columns: table => new
        {
            Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            Revision = table.Column<long>(type: "INTEGER", nullable: false),
            PolicyJson = table.Column<string>(type: "TEXT", nullable: false)
        },
        constraints: table => table.PrimaryKey("PK_BrowserPrivacy", x => x.Id));
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("BrowserPrivacy");
}
