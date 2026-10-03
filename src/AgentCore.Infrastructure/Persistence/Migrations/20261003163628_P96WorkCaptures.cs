using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class P96WorkCaptures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkCaptures",
                columns: table => new
                {
                    CaptureId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    WorkItemId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RetainUntilUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkCaptures", x => x.CaptureId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkCaptures_RetainUntilUtc",
                table: "WorkCaptures",
                column: "RetainUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WorkCaptures_WorkItemId",
                table: "WorkCaptures",
                column: "WorkItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkCaptures");
        }
    }
}
