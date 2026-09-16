using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AgentCoreDbContext))]
[Migration("20260916210000_Artifacts")]
public partial class Artifacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Artifacts",
            columns: table => new
            {
                ArtifactId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                BlobKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ContentType = table.Column<string>(type: "TEXT", nullable: false),
                ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                SourceAttachmentId = table.Column<string>(type: "TEXT", nullable: true),
                WorkspaceLogicalPath = table.Column<string>(type: "TEXT", nullable: true),
                CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Artifacts", x => x.ArtifactId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Artifacts_SessionId",
            table: "Artifacts",
            column: "SessionId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Artifacts");
    }
}
